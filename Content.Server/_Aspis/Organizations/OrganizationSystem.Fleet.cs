using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Shared._NF.Shipyard.Components;
using Content.Server._NF.Shipyard.Systems;
using Robust.Shared.IoC;
using Content.Shared.Radio;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Server._HL.Shipyard;
using Content.Shared._Triad.Shipyard.Save;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Content.Server.Station.Components;
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
    [Dependency] private ShipyardSystem _organizationFleetShipyard = default!;

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
        _fleetCardBindings.Clear();
    }

    private bool IsFleetShuttlePresent(Guid organization) =>
        _issuedFleetShuttles.TryGetValue(organization, out var issued) &&
        !TerminatingOrDeleted(issued.Grid) &&
        Transform(issued.Grid).MapUid is { Valid: true } currentMap &&
        !TerminatingOrDeleted(currentMap);

    // A rental listing is approved for an exact, server-verified blueprint hash.
    // Updating a blueprint never inherits the old design's approval.
    private bool IsFleetListingApproved(Organization org) =>
        IsValidOutpostHash(org.FleetBlueprintHash) &&
        string.Equals(org.FleetApprovedBlueprintHash, org.FleetBlueprintHash, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(org.FleetListingName);

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
            // Do not allow a previously approved rental to silently use a new grid.
            // A successful approval operation must explicitly bind to this digest.
            if (!string.Equals(org.FleetApprovedBlueprintHash, digest, StringComparison.Ordinal))
                org.FleetApprovedBlueprintHash = string.Empty;
        }))
        {
            Refresh(uid, actor, "Помилка реєстру. Попереднє креслення залишено без змін.");
            return;
        }
        _fleetErrors.Remove(org.Id);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Company fleet design staged: org={org.Id}, digest={digest}");
        Refresh(uid, actor, $"Перевірено креслення «{fileName}» (SHA-256: {digest[..10]}). Тепер затвердьте параметри оренди.");
    }

    private bool TryDeployFleetShuttle(Organization org, EntityUid console,
        out (EntityUid Map, EntityUid Grid) issued)
    {
        issued = default;
        // Use the station that owns THIS console, not an arbitrary sector station.
        // The regular Shipyard uses the same StationData -> largest grid route.
        var station = _organizationStationSystem.GetOwningStation(console);
        if (station is not { Valid: true } stationUid ||
            !TryComp<StationDataComponent>(stationUid, out var stationData) ||
            _organizationStationSystem.GetLargestGrid(stationData) is not { } stationGrid ||
            Transform(stationGrid).MapUid is not { Valid: true } stationMap)
        {
            _fleetErrors[org.Id] = "Консоль не належить станції з доступним стикувальним грідом.";
            return false;
        }

        if (!TryGetVerifiedOutpostYaml(org.Id, org.FleetBlueprintHash, out var yaml))
        {
            _fleetErrors[org.Id] = "Креслення відсутнє, пошкоджене або має недійсний підпис.";
            return false;
        }
        EntityUid? map = null;
        EntityUid? loadedGrid = null;
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
            loadedGrid = grid;
            if (Transform(grid).MapUid != map || Transform(grid).ParentUid != map)
            {
                _fleetErrors[org.Id] = "Завантажений шатл розміщено на неправильній мапі.";
                return false;
            }
            if (!TryComp<ShuttleComponent>(grid, out var shuttle))
            {
                _fleetErrors[org.Id] = "Креслення не містить керованого шатла.";
                return false;
            }
            EnsureComp<PhysicsComponent>(grid);
            EnsureComp<IFFComponent>(grid);
            _organizationFleetShipyard.PrepareCorporateLoadedGrid(grid);
            // A saved manifest must not import a previous owner's offline
            // auto-cleanup timer into an organization-owned rental.
            if (HasComp<ShipOwnershipComponent>(grid))
                RemComp<ShipOwnershipComponent>(grid);
            // Saved corporate rentals are not deed-backed personal property.
            // Prevent re-saving a leased grid through the ordinary shipyard console.
            EnsureComp<ShipSavingBlacklistComponent>(grid);
            // The shipyard sell-check looks at direct children of a grid rather than
            // its own components. An invisible map child is therefore required in
            // addition to the non-resellable deed. This survives the rented ID
            // being removed or unassigned at a shipyard console.
            var saleBlocker = Spawn(null, new EntityCoordinates(grid, Vector2.Zero));
            var sellCondition = EnsureComp<ShipyardSellConditionComponent>(saleBlocker);
            sellCondition.BlockSale = true;
            sellCondition.Reason = "shipyard-console-fallback-prevent-sale";
            // Same immediate FTL docking operation used by the ordinary Shipyard.
            // IMPORTANT: TryFTLDock can return false AFTER moving the grid near
            // the target as a proximity fallback. Proximity is NOT a rental success.
            if (!_organizationShuttle.TryFTLDock(grid, shuttle, stationGrid) ||
                Transform(grid).MapUid != stationMap ||
                !_constructionDocking.GetDocks(grid).Any(d =>
                    d.Comp.Docked && d.Comp.DockedWith is { } dockedTo &&
                    Transform(dockedTo).GridUid == stationGrid))
            {
                _fleetErrors[org.Id] = "На станції немає вільного сумісного шлюзу для цього шатла. Оренду не оформлено і гроші не списано.";
                return false;
            }
            var marker = EnsureComp<OrganizationFleetShuttleComponent>(grid);
            marker.OrganizationId = org.Id;
            RestoreOutpostCompanySecurity(org.Id, grid);
            // The staging map is now EMPTY: the shuttle was transferred to the
            // station's shared sector map by TryFTLDock. Never expose its empty
            // map as an FTL destination and never delete the station map.
            _organizationMetadata.SetEntityName(map.Value, $"Тимчасовий док: {org.Name}");
            _organizationMetadata.SetEntityName(grid,
                string.IsNullOrWhiteSpace(org.FleetListingName) ? org.FleetBlueprintName : org.FleetListingName);
            issued = (map.Value, grid);
            _fleetErrors.Remove(org.Id);
            success = true;
            QueueDel(map.Value);
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
            if (!success)
            {
                // A failed TryFTLDock may already have moved the vessel onto
                // the station map. Delete that grid as well as the staging map.
                if (loadedGrid is { } rejected && !TerminatingOrDeleted(rejected))
                {
                    _constructionDocking.UndockDocks(rejected);
                    QueueDel(rejected);
                }
                if (map is { } created && !TerminatingOrDeleted(created))
                    QueueDel(created);
            }
        }
    }

    private void CleanupFailedFleetRental((EntityUid Map, EntityUid Grid) issued)
    {
        // The staging map was deleted after the successful dock. The grid is
        // now on the station's map, which must NEVER be deleted here.
        if (!TerminatingOrDeleted(issued.Grid))
        {
            _constructionDocking.UndockDocks(issued.Grid);
            QueueDel(issued.Grid);
        }
        if (!TerminatingOrDeleted(issued.Map))
            QueueDel(issued.Map);
    }

    private void OnConfigureFleetListing(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationConfigureFleetListingMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            !_store.Available || _fleetUsedThisRound.Contains(org.Id))
            return;
        if (!IsFleetBlueprintReady(org))
        {
            Refresh(uid, actor, "Перед затвердженням дочекайтеся успішної перевірки вибраного креслення сервером.");
            return;
        }
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
                org.FleetApprovedBlueprintHash = org.FleetBlueprintHash;
            }))
        {
            Refresh(uid, actor, "Не вдалося затвердити умови оренди.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Fleet listing configured: org={org.Id}, rental={args.RentalPrice}");
        Refresh(uid, actor, $"Затверджено оренду «{name}» саме для креслення {org.FleetBlueprintName} (SHA-256: {org.FleetBlueprintHash[..10]}). Тепер шатл можна орендувати.");
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
        // A shuttle can FTL to another map after renting. Examine its
        // CURRENT grid, not the temporary map where it was first spawned.
        // Never delete a rented grid containing players, and never delete the
        // shared sector map that the grid travelled to.
        // The original staging map is already deleted. The grid can travel by FTL.
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player ||
                TerminatingOrDeleted(player))
                continue;
            var transform = Transform(player);
            if (transform.GridUid == issued.Grid)
            {
                Refresh(uid, actor, "Перед поверненням усі гравці мають залишити шатл.");
                return;
            }
        }
        _fleetReturnedThisRound.Add(org.Id);
        if (_fleetCardBindings.Remove(org.Id, out var boundCard) &&
            !TerminatingOrDeleted(boundCard))
        {
            // An ID might have been removed from the console, handed over, or
            // unassigned at Shipyard. Only clean up the deed from THIS rental;
            // never erase an unrelated, newly purchased vessel on the same ID.
            _organizationFleetShipyard.RemoveCorporateRentalDeed(boundCard, issued.Grid);
            if (TryComp<OrganizationLeaseCardBindingComponent>(boundCard, out var binding) &&
                binding.Organization == org.Id && binding.Grid == issued.Grid)
                RemComp<OrganizationLeaseCardBindingComponent>(boundCard);
        }
        _issuedFleetShuttles.Remove(org.Id);
        // Never delete the station/shared sector map; only remove the rental.
        CleanupFailedFleetRental(issued);
        if (_prototypes.TryIndex<RadioChannelPrototype>("Traffic", out var shortband))
            _radio.SendRadioMessage(uid,
                $"ВЕРФ / ОРЕНДА: організація «{org.Name}» повернула шатл. " +
                $"Орендар: {_fleetRenterNames.GetValueOrDefault(org.Id, "невідомий")}. " +
                "Видачу завершено до наступного раунду.", shortband, uid);
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
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;
        if (!HasCompletedCorporateWaitingPeriod(member))
        {
            Refresh(uid, actor, "Оренда доступна через 15 хвилин після вступу до організації.");
            return;
        }
        if (!TryComp<OrganizationConsoleLeaseSlotComponent>(uid, out var idSlot) ||
            idSlot.CardSlot.Item is not { Valid: true } presentedCard ||
            !TryComp<Content.Shared.Access.Components.IdCardComponent>(presentedCard, out _) ||
            !TryComp<OrganizationAccessCardComponent>(presentedCard, out var corporateCard) ||
            corporateCard.Organization != org.Id ||
            !TryComp<OrganizationIssuedCardIdentityComponent>(presentedCard, out var identity) ||
            identity.UserId != character.UserId || identity.Slot != character.Slot)
        {
            Refresh(uid, actor, "Вставте у слот консолі власну ID-картку цієї організації.");
            return;
        }
        // Shipyard uses one deed per ID. Do not silently overwrite a bought
        // ship or bind a single physical card to multiple company rentals.
        if (HasComp<ShuttleDeedComponent>(presentedCard) ||
            HasComp<OrganizationLeaseCardBindingComponent>(presentedCard))
        {
            Refresh(uid, actor, "На цій ID-картці вже зареєстровано шатл. Використайте іншу ID-картку організації.");
            return;
        }
        if (member.Role != OrganizationMemberRole.Owner &&
            !_sectorQuotaClaims.Values.Any(q => q.Organization == org.Id && !q.Completed))
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
            org.GrossRevenue > long.MaxValue - rentalPrice ||
            !IsFleetListingApproved(org) || !IsFleetBlueprintReady(org))
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
        if (!TryDeployFleetShuttle(org, uid, out var issued))
        {
            Refresh(uid, actor, "Шатл не створено. Гроші не списані. Перевірте повідомлення у флоті.");
            return;
        }
        // Register the ordinary shipyard deed BEFORE charging. If the ID is
        // already associated with another ship, abort without modifying money.
        var shuttleName = string.IsNullOrWhiteSpace(org.FleetListingName)
            ? org.FleetBlueprintName : org.FleetListingName;
        if (!_organizationFleetShipyard.TryRegisterCorporateRentalDeed(
                presentedCard, issued.Grid, shuttleName, renterName))
        {
            CleanupFailedFleetRental(issued);
            Refresh(uid, actor, "Не вдалося записати шатл на ID-картку. Оплату не списано.");
            return;
        }
        // Physical spawn is not an account transfer. Renter pays only after the grid
        // exists; a failed durable save results in an immediate bank refund attempt.
        if (!_bank.TryBankWithdraw(actor, rentalPrice))
        {
            _organizationFleetShipyard.RemoveCorporateRentalDeed(presentedCard, issued.Grid);
            CleanupFailedFleetRental(issued);
            Refresh(uid, actor, "Оплату не прийнято. Шатл не видано.");
            return;
        }
        if (!Commit(() =>
        {
            org.Treasury += rentalPrice;
            org.GrossRevenue += rentalPrice;
        }))
        {
            _organizationFleetShipyard.RemoveCorporateRentalDeed(presentedCard, issued.Grid);
            CleanupFailedFleetRental(issued);
            if (!_bank.TryBankDeposit(actor, rentalPrice))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"URGENT: Fleet rental refund requires administrator reconciliation: org={org.Id}, amount={rentalPrice}, user={character.UserId}");
            Refresh(uid, actor, "Помилка запису оренди. Перевірте баланс, у разі потреби зверніться до адміністратора.");
            return;
        }
        var rentalMarker = EnsureComp<OrganizationFleetShuttleComponent>(issued.Grid);
        rentalMarker.RenterUserId = character.UserId;
        rentalMarker.RenterSlot = character.Slot;
        var binding = EnsureComp<OrganizationLeaseCardBindingComponent>(presentedCard);
        binding.Organization = org.Id;
        binding.Grid = issued.Grid;

        _fleetCardBindings[org.Id] = presentedCard;
        _issuedFleetShuttles[org.Id] = issued;
        _fleetUsedThisRound.Add(org.Id);
        _fleetIssuedRenters[org.Id] = character;
        _fleetRenterNames[org.Id] = renterName;
        _fleetIssuedNames[org.Id] = shuttleName;
        // Apply corporate name/color immediately; the regular IFF reconciler
        // keeps them in sync during the rest of the round.
        ApplyOrganizationShipIFF(issued.Grid, org);
        // Traffic is the Shortband radio channel used by standard Shipyard.
        if (_prototypes.TryIndex<RadioChannelPrototype>("Traffic", out var shortband))
            _radio.SendRadioMessage(uid,
                $"ВЕРФ / ОРЕНДА: шатл «{shuttleName}» видано організації «{org.Name}». " +
                $"Орендар: {renterName}. Транспорт зареєстровано на ID-картку орендаря. " +
                "Продаж через Shipyard заборонено.", shortband, uid);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Organization fleet shuttle leased: org={org.Id}, renter={character.UserId}, slot={character.Slot}, price={rentalPrice}");
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor, "Шатл видано на раунд і прив’язано до ID. Вийміть картку з консолі. Оплату отримано з вашого рахунку.");
    }
}
