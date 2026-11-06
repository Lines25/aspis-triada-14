using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Server._HL.Shipyard;
using Content.Shared._Triad.Shipyard.Save;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Robust.Shared.Physics.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Organization fleet: owner-staged, signature-checked ship templates and one
/// round-local shuttle lease per organization. No network-supplied server paths.
/// The server charges the renter after successful map/grid/FTL setup and
/// records payment to the organization only after the withdrawal succeeds.
/// </summary>
public sealed partial class OrganizationSystem
{
    private readonly Dictionary<Guid, (EntityUid Map, EntityUid Grid)> _issuedFleetShuttles = new();
    private readonly HashSet<Guid> _fleetUsedThisRound = new();
    private readonly Dictionary<Guid, string> _fleetIssuedNames = new();
    // The character taking the lease is recorded by the server, never accepted from UI.
    private readonly Dictionary<Guid, CharacterKey> _fleetIssuedRenters = new();
    private readonly Dictionary<Guid, string> _fleetRenterNames = new();
    private readonly HashSet<Guid> _fleetReturnedThisRound = new();
    private readonly Dictionary<Guid, string> _fleetErrors = new();
    private readonly Dictionary<Guid, TimeSpan> _fleetLastUpload = new();
    private readonly Dictionary<Guid, TimeSpan> _fleetLastRentalAttempt = new();

    private void ClearRoundFleetReferences()
    {
        // GameTicker owns the round's maps; never persist or reuse their UIDs.
        _issuedFleetShuttles.Clear();
        _fleetUsedThisRound.Clear();
        _fleetIssuedNames.Clear();
        _fleetIssuedRenters.Clear();
        _fleetRenterNames.Clear();
        _fleetReturnedThisRound.Clear();
        _fleetErrors.Clear();
        _fleetLastRentalAttempt.Clear();
        _fleetLastUpload.Clear();
    }

    private bool IsFleetShuttlePresent(Guid organization) =>
        _issuedFleetShuttles.TryGetValue(organization, out var issued) &&
        !TerminatingOrDeleted(issued.Map) && !TerminatingOrDeleted(issued.Grid);

    private bool IsFleetBlueprintReady(Organization org)
    {
        if (!IsValidOutpostHash(org.FleetBlueprintHash) ||
            string.IsNullOrEmpty(org.FleetBlueprintName))
            return false;
        // Do not use the outpost blueprint cache, as fleets and outposts are separate.
        return TryGetVerifiedOutpostYaml(org.Id, org.FleetBlueprintHash, out _);
    }

    private void OnStageFleetBlueprint(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationStageFleetBlueprintMessage args) =>
        StageFleetBlueprint(uid, args.Actor, args.Organization, args.Name, args.Envelope);

    private void StageFleetBlueprint(EntityUid uid, EntityUid actor, Guid organizationId,
        string fileName, string envelope)
    {
        if (!TryGetOutpostOwner(uid, actor, organizationId, out var org))
            return;
        if (_fleetUsedThisRound.Contains(org.Id))
        {
            Refresh(uid, actor, "Зміна креслення доступна з наступного раунду.");
            return;
        }
        if (_fleetLastUpload.TryGetValue(org.Id, out var previous) &&
            _gameTiming.CurTime < previous + TimeSpan.FromSeconds(30))
        {
            Refresh(uid, actor, "Креслення флоту можна змінювати раз на 30 секунд.");
            return;
        }
        _fleetLastUpload[org.Id] = _gameTiming.CurTime;
        if (envelope is null || envelope.Length is < 100 or > MaxOutpostEnvelopeLength ||
            fileName is null || fileName.Length is < 1 or > 64 || fileName.Any(char.IsControl) ||
            !TryCanonicalizeOutpostEnvelope(envelope, out var canonical))
        {
            Refresh(uid, actor, "Потрібен справжній підписаний файл шатла цього сервера.");
            return;
        }
        var digest = ComputeEnvelopeHash(canonical);
        var path = OutpostBlueprintPath(org.Id, digest);
        try
        {
            _resources.UserData.CreateDir(OutpostBlueprintDirectory);
            if (!_resources.UserData.Exists(path))
            {
                using var writer = _resources.UserData.OpenWriteText(path);
                writer.Write(canonical);
            }
            if (!_resources.UserData.TryReadAllText(path, out var readBack) ||
                ComputeEnvelopeHash(readBack) != digest ||
                !string.Equals(readBack, canonical, StringComparison.Ordinal))
            {
                Refresh(uid, actor, "Сервер не зміг підтвердити цілісність креслення флоту.");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.GetSawmill("organizations.fleet").Error($"Fleet staging failed: {ex}");
            Refresh(uid, actor, "Не вдалося зберегти креслення на сервері.");
            return;
        }
        if (!Commit(() =>
        {
            org.FleetBlueprintName = fileName.Trim();
            org.FleetBlueprintHash = digest;
        }))
        {
            Refresh(uid, actor, "Помилка реєстру. Попереднє креслення залишено без змін.");
            return;
        }
        _fleetErrors.Remove(org.Id);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Company fleet design staged: org={org.Id}, digest={digest}");
        Refresh(uid, actor, "Шатл додано до флоту. Він буде доступний учасникам із квотою.");
    }

    private bool TryDeployFleetShuttle(Organization org, out (EntityUid Map, EntityUid Grid) issued)
    {
        issued = default;
        if (!TryGetVerifiedOutpostYaml(org.Id, org.FleetBlueprintHash, out var yaml))
        {
            _fleetErrors[org.Id] = "Креслення відсутнє, пошкоджене або має недійсний підпис.";
            return false;
        }
        EntityUid? map = null;
        var success = false;
        try
        {
            map = _organizationMapSystem.CreateMap(out var mapId);
            yaml = ShipSaveYamlSanitizer.ScrubShipLoadYaml(yaml, out _);
            using var reader = new StringReader(yaml);
            if (!_organizationGridLoader.TryLoadGrid(mapId, reader,
                    $"organization-fleet:{org.Id:N}", out var loaded) || loaded is null)
            {
                _fleetErrors[org.Id] = "Неможливо завантажити YAML-сітку. Спробуйте зберегти шатл у поточній версії.";
                return false;
            }
            var grid = loaded.Value.Owner;
            if (Transform(grid).MapUid != map || Transform(grid).ParentUid != map)
            {
                _fleetErrors[org.Id] = "Завантажений шатл розміщено на неправильній мапі.";
                return false;
            }
            if (!TryComp<ShuttleComponent>(grid, out _))
            {
                _fleetErrors[org.Id] = "Креслення не містить керованого шатла.";
                return false;
            }
            EnsureComp<PhysicsComponent>(grid);
            EnsureComp<IFFComponent>(grid);
            // Saved corporate rentals are not deed-backed personal property.
            // Prevent re-saving a leased grid through the ordinary shipyard console.
            EnsureComp<ShipSavingBlacklistComponent>(grid);
            if (!_organizationShuttle.TryAddFTLDestination(mapId, true, false, false, out _))
            {
                _fleetErrors[org.Id] = "Не вдалося зареєструвати маршрут до орендованого шатла.";
                return false;
            }
            var marker = EnsureComp<OrganizationFleetShuttleComponent>(grid);
            marker.OrganizationId = org.Id;
            _organizationMetadata.SetEntityName(map.Value, $"Шатл компанії: {org.Name}");
            _organizationMetadata.SetEntityName(grid,
                string.IsNullOrWhiteSpace(org.FleetListingName) ? org.FleetBlueprintName : org.FleetListingName);
            issued = (map.Value, grid);
            _fleetErrors.Remove(org.Id);
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            _fleetErrors[org.Id] = $"Завантаження шатла завершилось помилкою {ex.GetType().Name}. Подробиці в журналі.";
            _log.GetSawmill("organizations.fleet").Error($"Fleet rental deployment failed for {org.Id}: {ex}");
            return false;
        }
        finally
        {
            if (!success && map is { } created && !TerminatingOrDeleted(created))
                QueueDel(created);
        }
    }

    private void OnConfigureFleetListing(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationConfigureFleetListingMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            !_store.Available || _fleetUsedThisRound.Contains(org.Id))
            return;
        var name = args.Name?.Trim() ?? string.Empty;
        var description = args.Description?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 48 || name.Any(char.IsControl) ||
            description.Length > 160 || description.Any(char.IsControl) ||
            args.RentalPrice is < OrganizationFleetRules.MinimumRentalPrice
                or > OrganizationFleetRules.MaximumRentalPrice)
        {
            Refresh(uid, actor, "Назва 2-48 знаків, опис до 160 знаків, ціна 1 000-20 000 $." );
            return;
        }
        if (!Commit(() =>
            {
                org.FleetListingName = name;
                org.FleetListingDescription = description;
                org.FleetRentalPrice = args.RentalPrice;
            }))
        {
            Refresh(uid, actor, "Не вдалося затвердити умови оренди.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Fleet listing configured: org={org.Id}, rental={args.RentalPrice}");
        Refresh(uid, actor, "Умови оренди шатла збережено.");
    }

    private void OnReturnFleetShuttle(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationReturnFleetShuttleMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !_outpostRoundStarted ||
            !TryGetCharacter(actor, out var character, out _) || !IsOriginalController(actor) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            !org.Members.ContainsKey(character) ||
            !_fleetIssuedRenters.TryGetValue(org.Id, out var originalRenter) ||
            character != originalRenter ||
            !_issuedFleetShuttles.TryGetValue(org.Id, out var issued))
            return;
        if (!IsFleetShuttlePresent(org.Id))
        {
            Refresh(uid, actor, "Шатл більше не існує. Повернути його неможливо.");
            return;
        }
        // Never delete a map containing an online player, regardless of company membership.
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player ||
                TerminatingOrDeleted(player))
                continue;
            if (Transform(player).MapUid == issued.Map)
            {
                Refresh(uid, actor, "Перед поверненням усі гравці мають залишити шатл.");
                return;
            }
        }
        _fleetReturnedThisRound.Add(org.Id);
        _issuedFleetShuttles.Remove(org.Id);
        if (!TerminatingOrDeleted(issued.Map))
            QueueDel(issued.Map);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Fleet lease returned by renter: org={org.Id}, actor={character.UserId}, slot={character.Slot}");
        Refresh(uid, actor, "Шатл повернуто. Оренда оплачена за весь раунд, повторно видати його не можна.");
    }

    private void OnRentFleetShuttle(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationRentFleetShuttleMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !_store.Available || !_outpostRoundStarted ||
            !TryGetCharacter(actor, out var character, out var renterName) || !IsOriginalController(actor) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            !org.Members.TryGetValue(character, out var member) ||
            member.Role == OrganizationMemberRole.Owner ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;
        if (!_sectorQuotaClaims.Values.Any(q => q.Organization == org.Id && !q.Completed))
        {
            Refresh(uid, actor, "Для видачі транспорту спочатку прийміть квоту.");
            return;
        }
        if (_fleetUsedThisRound.Contains(org.Id))
        {
            Refresh(uid, actor, "Організація вже використала оренду в цьому раунді.");
            return;
        }
        if (_issuedFleetShuttles.Count >= OrganizationFleetRules.MaximumSimultaneousRentals)
        {
            Refresh(uid, actor, "Ліміт активних корпоративних шатлів у секторі вичерпано.");
            return;
        }
        var rentalPrice = org.FleetRentalPrice;
        if (rentalPrice is < OrganizationFleetRules.MinimumRentalPrice
                or > OrganizationFleetRules.MaximumRentalPrice ||
            org.Treasury > MaxTreasury - rentalPrice ||
            org.GrossRevenue > long.MaxValue - rentalPrice || !IsFleetBlueprintReady(org))
        {
            Refresh(uid, actor, "Пропозиція оренди недоступна або скарбниця переповнена.");
            return;
        }
        if (!TryComp<Content.Shared._NF.Bank.Components.BankAccountComponent>(actor, out var account) ||
            account.Balance < rentalPrice)
        {
            Refresh(uid, actor, "Недостатньо коштів на особистому рахунку для оренди.");
            return;
        }
        if (_fleetLastRentalAttempt.TryGetValue(org.Id, out var last) &&
            _gameTiming.CurTime < last + TimeSpan.FromSeconds(30))
        {
            Refresh(uid, actor, "Повторіть спробу оренди через 30 секунд.");
            return;
        }
        _fleetLastRentalAttempt[org.Id] = _gameTiming.CurTime;
        if (!TryDeployFleetShuttle(org, out var issued))
        {
            Refresh(uid, actor, "Шатл не створено. Гроші не списані. Перевірте повідомлення у флоті.");
            return;
        }
        // Physical spawn is not an account transfer. Renter pays only after the grid
        // exists; a failed durable save results in an immediate bank refund attempt.
        if (!_bank.TryBankWithdraw(actor, rentalPrice))
        {
            if (!TerminatingOrDeleted(issued.Map)) QueueDel(issued.Map);
            Refresh(uid, actor, "Оплату не прийнято. Шатл не видано.");
            return;
        }
        if (!Commit(() =>
        {
            org.Treasury += rentalPrice;
            org.GrossRevenue += rentalPrice;
        }))
        {
            if (!TerminatingOrDeleted(issued.Map)) QueueDel(issued.Map);
            if (!_bank.TryBankDeposit(actor, rentalPrice))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"URGENT: Fleet rental refund requires administrator reconciliation: org={org.Id}, amount={rentalPrice}, user={character.UserId}");
            Refresh(uid, actor, "Помилка запису оренди. Перевірте баланс, у разі потреби зверніться до адміністратора.");
            return;
        }
        var rentalMarker = EnsureComp<OrganizationFleetShuttleComponent>(issued.Grid);
        rentalMarker.RenterUserId = character.UserId;
        rentalMarker.RenterSlot = character.Slot;
        _issuedFleetShuttles[org.Id] = issued;
        _fleetUsedThisRound.Add(org.Id);
        _fleetIssuedRenters[org.Id] = character;
        _fleetRenterNames[org.Id] = renterName;
        _fleetIssuedNames[org.Id] = string.IsNullOrWhiteSpace(org.FleetListingName)
            ? org.FleetBlueprintName : org.FleetListingName;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Organization fleet shuttle leased: org={org.Id}, renter={character.UserId}, slot={character.Slot}, price={rentalPrice}");
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor, "Шатл видано на раунд. Оплату отримано з вашого рахунку.");
    }
}
