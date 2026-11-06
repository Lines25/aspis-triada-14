using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Robust.Shared.Map.Components;
using System.Security.Cryptography;
using System.IO;
using Robust.Shared.IoC;
using System.Text;
using Content.Server._Triad.Shipyard;
using Content.Server._HL.Shipyard;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Server.Shuttles.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Permanent outpost blueprint intake (stage one of deployment).
///
/// Saves a trusted ship envelope into server-owned, content-addressed UserData.
/// No bank charge, map spawn, influence bonus or construction state is granted
/// until the subsequent authoritative deployment implementation is available.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const int MaxOutpostEnvelopeLength = OrganizationBlueprintUploadRules.MaxEnvelopeCharacters;
    private const int MaxOutpostShipYamlLength = 10_000_000;
    private static readonly TimeSpan OutpostUploadCooldown = TimeSpan.FromSeconds(30);
    private static readonly ResPath OutpostBlueprintDirectory = new("/Organizations/OutpostBlueprints");

    [Dependency] private TriadTamperPolicyService _outpostTamperPolicy = default!;

    private readonly Dictionary<Guid, TimeSpan> _lastOutpostUpload = new();
    // Hash of last VERIFIED envelope. Never cache a failed signature check:
    // shipyard key bootstrap may finish after the registry has restored.
    private readonly Dictionary<Guid, string> _verifiedOutpostBlueprints = new();

    // Entity UIDs are round-local. Never serialize them to OrganizationStore.
    private readonly Dictionary<Guid, (EntityUid Map, EntityUid Grid)> _spawnedOrganizationOutposts = new();
    // A station entity is round-local and must never be persisted.
    private readonly Dictionary<Guid, EntityUid> _outpostStations = new();
    private bool _outpostRoundStarted;
    private const int MaximumPurchasedOutposts = 16;
    private readonly Dictionary<Guid, TimeSpan> _lastOutpostRedeployAttempt = new();
    private readonly Dictionary<Guid, string> _outpostDeploymentErrors = new();
    private static readonly TimeSpan OutpostReplacementDelay =
        TimeSpan.FromMinutes(OrganizationOutpostRules.ReplacementWaitMinutes);

    [Dependency] private SharedMapSystem _organizationMapSystem = default!;
    [Dependency] private MapLoaderSystem _organizationGridLoader = default!;
    [Dependency] private ShuttleSystem _organizationShuttle = default!;
    [Dependency] private MetaDataSystem _organizationMetadata = default!;
    [Dependency] private StationSystem _organizationStationSystem = default!;
    [Dependency] private SharedTransformSystem _organizationOutpostTransforms = default!;

    private static bool IsValidOutpostHash(string? digest) =>
        digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit);

    private static ResPath OutpostBlueprintPath(Guid organization, string hash) =>
        OutpostBlueprintDirectory / $"{organization:N}-{hash}.yml";

    private static string ComputeEnvelopeHash(string envelope) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope)));

    private bool TryCanonicalizeOutpostEnvelope(string envelope, out string canonical)
    {
        canonical = string.Empty;
        if (envelope.Length is < 100 or > MaxOutpostEnvelopeLength)
            return false;

        try
        {
            var signed = AuthenticatedShipFile.FromShipFile(envelope);
            // Enforce THIS server's signing key, independent of shipyard notify mode.
            if (!_outpostTamperPolicy.IsTrustedForPermanentOutpost(signed))
                return false;

            var yaml = signed.ShipYamlString();
            if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxOutpostShipYamlLength)
                return false;

            // Appraisal is intentionally NOT signed by shipyard. Normalize it away,
            // along with arbitrary envelope formatting, before making a storage key.
            // Otherwise a user can fill server disk by editing only appraisal/spacing.
            signed.Appraisal = null;
            canonical = signed.ShipFileString();
            return canonical.Length is >= 100 and <= MaxOutpostEnvelopeLength;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks persistence and provenance. This must also be called again by the
    /// future map deployment path immediately before it loads the blueprint.
    /// </summary>
    private bool IsOutpostBlueprintReady(Organization organization)
    {
        if (!IsValidOutpostHash(organization.OutpostBlueprintHash) ||
            organization.OutpostBlueprintName.Length == 0)
            return false;

        if (_verifiedOutpostBlueprints.TryGetValue(organization.Id, out var verified) &&
            verified == organization.OutpostBlueprintHash)
            return true;

        try
        {
            var path = OutpostBlueprintPath(organization.Id, organization.OutpostBlueprintHash);
            if (!_resources.UserData.TryReadAllText(path, out var contents) ||
                ComputeEnvelopeHash(contents) != organization.OutpostBlueprintHash ||
                !TryCanonicalizeOutpostEnvelope(contents, out var canonical) ||
                !string.Equals(contents, canonical, StringComparison.Ordinal))
                return false;

            _verifiedOutpostBlueprints[organization.Id] = organization.OutpostBlueprintHash;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnStageOutpostBlueprint(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationStageOutpostBlueprintMessage args) =>
        StageOutpostBlueprint(uid, args.Actor, args.Organization, args.Name, args.Envelope);

    private void StageOutpostBlueprint(EntityUid uid, EntityUid actor, Guid organizationId,
        string fileName, string envelope)
    {
        if (!AuthorizedBusiness(uid, actor) || !_store.Available ||
            !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(organizationId, out var organization) ||
            organization.Owner != key || organization.InsolvencyState != OrganizationInsolvencyState.Active)
            return;

        if (_lastOutpostUpload.TryGetValue(organization.Id, out var last) &&
            _gameTiming.CurTime - last < OutpostUploadCooldown)
        {
            Refresh(uid, actor, "Креслення можна надсилати раз на 30 секунд.");
            return;
        }
        // Apply rate limit before parsing untrusted content.
        _lastOutpostUpload[organization.Id] = _gameTiming.CurTime;

        if (envelope is null || envelope.Length is < 100 or > MaxOutpostEnvelopeLength ||
            fileName is null || fileName.Length is < 1 or > 64 ||
            fileName.Any(char.IsControl))
        {
            Refresh(uid, actor, "Некоректна назва або завеликий файл креслення.");
            return;
        }
        if (!TryCanonicalizeOutpostEnvelope(envelope, out var canonical))
        {
            Refresh(uid, actor, "Відхилено: потрібен справжній підписаний файл шатла з цього сервера.");
            return;
        }

        // The filename comes ONLY from the organization UUID and canonical digest.
        // Never use a client-supplied filesystem path (or the displayed ship name).
        var digest = ComputeEnvelopeHash(canonical);
        var path = OutpostBlueprintPath(organization.Id, digest);
        try
        {
            _resources.UserData.CreateDir(OutpostBlueprintDirectory);
            if (!_resources.UserData.Exists(path))
            {
                using var writer = _resources.UserData.OpenWriteText(path);
                writer.Write(canonical);
            }
            if (!_resources.UserData.TryReadAllText(path, out var saved) ||
                ComputeEnvelopeHash(saved) != digest)
            {
                Refresh(uid, actor, "Сервер не зміг перевірити записану копію креслення.");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.GetSawmill("organizations.outposts").Error($"Outpost blueprint storage failed: {ex}");
            Refresh(uid, actor, "Сервер не зміг зберегти креслення.");
            return;
        }

        var name = fileName.Trim();
        if (!Commit(() =>
            {
                organization.OutpostBlueprintName = name;
                organization.OutpostBlueprintHash = digest;
                organization.OutpostBlueprintUtc = DateTime.UtcNow;
            }))
        {
            // Content-addressed files are never overwritten. If registry commit
            // fails, the old pointer remains authoritative; the new file is orphaned.
            Refresh(uid, actor, "Не вдалося зберегти запис у реєстрі аванпостів.");
            return;
        }

        _verifiedOutpostBlueprints[organization.Id] = digest;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Outpost blueprint staged by {ToPrettyString(actor):player} for org={organization.Id}, digest={digest}, ship={name}");
        Refresh(uid, actor, "Креслення перевірено й збережено сервером. Аванпост ще не побудовано, кошти не списані.");
    }
    /// <summary>
    /// Authoritative, content-addressed read for EACH deployment. A previous UI
    /// verification is insufficient: signed files can be lost or keys can rotate.
    /// </summary>
    private bool TryGetVerifiedOutpostYaml(Guid organizationId, string hash, out string yaml)
    {
        yaml = string.Empty;
        if (!IsValidOutpostHash(hash))
            return false;

        try
        {
            if (!_resources.UserData.TryReadAllText(OutpostBlueprintPath(organizationId, hash), out var stored) ||
                stored.Length > MaxOutpostEnvelopeLength ||
                ComputeEnvelopeHash(stored) != hash ||
                !TryCanonicalizeOutpostEnvelope(stored, out var canonical) ||
                !string.Equals(stored, canonical, StringComparison.Ordinal))
                return false;

            yaml = AuthenticatedShipFile.FromShipFile(canonical).ShipYamlString();
            return yaml.Length is > 0 and <= MaxOutpostShipYamlLength;
        }
        catch (Exception ex)
        {
            _log.GetSawmill("organizations.outposts").Warning($"Cannot read outpost blueprint {organizationId}: {ex}");
            return false;
        }
    }

    private bool IsOutpostPhysicallyPresent(Guid orgId)
    {
        return _spawnedOrganizationOutposts.TryGetValue(orgId, out var entry) &&
            !TerminatingOrDeleted(entry.Map) && !TerminatingOrDeleted(entry.Grid) &&
            Transform(entry.Grid).ParentUid == entry.Map &&
            Transform(entry.Grid).MapUid == entry.Map;
    }

    /// <summary>
    /// Deploy signed corporate blueprints into the SAME space map as the sector's
    /// principal station. A purchased outpost is a real StationData station, but
    /// intentionally has no public StationJobs until role-scoped spawning is
    /// configured. This prevents strangers from joining an unverified player map.
    /// The shared sector map must NEVER be deleted on failed deployment.
    /// </summary>
    private bool TrySpawnOrganizationOutpost(Organization org, string hash,
        out (EntityUid Map, EntityUid Grid) deployed)
    {
        deployed = default;
        if (!TryGetVerifiedOutpostYaml(org.Id, hash, out var shipYaml))
        {
            _outpostDeploymentErrors[org.Id] = "Сервер не зміг перевірити підпис або прочитати креслення.";
            return false;
        }

        var principal = _organizationStationSystem.GetStations()
            .Where(station => !HasComp<OrganizationOutpostStationComponent>(station))
            .Where(station => TryComp<StationDataComponent>(station, out var data) &&
                              data.Grids.Count > 0)
            .OrderByDescending(station => Name(station).Contains("Venmar Station", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(station => _organizationStationSystem.GetTileCount(Comp<StationDataComponent>(station)))
            .FirstOrDefault();
        if (!principal.Valid)
        {
            _outpostDeploymentErrors[org.Id] = "Основна станція сектора ще не завантажена.";
            return false;
        }
        var sectorGrid = _organizationStationSystem.GetLargestGrid(Comp<StationDataComponent>(principal));
        if (sectorGrid is not { } anchor || Transform(anchor).MapUid is not { } sectorMap)
        {
            _outpostDeploymentErrors[org.Id] = "Не знайдено спільну космічну мапу сектора.";
            return false;
        }
        var mapId = Transform(anchor).MapID;

        EntityUid? loadedGrid = null;
        EntityUid? createdStation = null;
        var success = false;
        try
        {
            shipYaml = ShipSaveYamlSanitizer.ScrubShipLoadYaml(shipYaml, out var scrubbed);
            if (scrubbed > 0)
                _log.GetSawmill("organizations.outposts").Info($"Scrubbed {scrubbed} stale nodes for {org.Id}");
            using var reader = new StringReader(shipYaml);
            if (!_organizationGridLoader.TryLoadGrid(mapId, reader,
                    $"organization-outpost:{org.Id:N}", out var result) || result is null)
            {
                _outpostDeploymentErrors[org.Id] = "MapLoader відхилив YAML корпоративної станції.";
                return false;
            }

            loadedGrid = result.Value.Owner;
            if (Transform(loadedGrid.Value).MapUid != sectorMap ||
                !TryComp<MapGridComponent>(loadedGrid.Value, out var grid))
            {
                _outpostDeploymentErrors[org.Id] = "Креслення завантажилося поза основним сектором.";
                return false;
            }

            // Use an isolated allocation for each corporate grid on the shared map.
            // Positions are allocated by width, not by an untrusted location from the blueprint.
            var occupied = _spawnedOrganizationOutposts.Values
                .Where(entry => !TerminatingOrDeleted(entry.Grid) &&
                                Transform(entry.Grid).MapUid == sectorMap)
                .Select(entry => Transform(entry.Grid).WorldPosition.X +
                                 (TryComp<MapGridComponent>(entry.Grid, out var other)
                                     ? MathF.Max(500f, other.LocalAABB.Width) : 500f))
                .DefaultIfEmpty(4000f).Max();
            var nextX = occupied + MathF.Max(500f, grid.LocalAABB.Width) + 500f;
            _organizationOutpostTransforms.SetWorldPosition(loadedGrid.Value, new Vector2(nextX, 4000f));

            // Only stations with verified beacons in their signed blueprint become
            // login destinations. Player-built maps without a safe marker remain
            // sector stations but are not exposed to the lobby/late-join picker.
            var joinable = TryBindOutpostSpawnBeacons(org.Id, loadedGrid.Value);
            createdStation = Spawn(joinable
                ? "AspisOrganizationOutpostJoinableStation"
                : "AspisOrganizationOutpostStation", Robust.Shared.Map.MapCoordinates.Nullspace);
            _organizationStationSystem.RenameStation(createdStation.Value, OutpostStationTitle(org), false);
            _organizationStationSystem.AddGridToStation(createdStation.Value, loadedGrid.Value);
            EnsureComp<OrganizationOutpostStationComponent>(createdStation.Value).OrganizationId = org.Id;
            EnsureComp<OrganizationOutpostComponent>(loadedGrid.Value).OrganizationId = org.Id;
            _organizationMetadata.SetEntityName(loadedGrid.Value, OutpostStationTitle(org));

            // Corporate station slots are never eligible for random round-start
            // assignment. Server-side membership checks guard deliberate late join.
            _outpostStations[org.Id] = createdStation.Value;
            deployed = (sectorMap, loadedGrid.Value);
            _outpostDeploymentErrors.Remove(org.Id);
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            _outpostDeploymentErrors[org.Id] = $"Помилка розгортання ({ex.GetType().Name}).";
            _log.GetSawmill("organizations.outposts").Error($"Outpost deployment failed for {org.Id}: {ex}");
            return false;
        }
        finally
        {
            if (!success)
            {
                if (createdStation is { } station && !TerminatingOrDeleted(station))
                    QueueDel(station);
                if (loadedGrid is { } gridUid && !TerminatingOrDeleted(gridUid))
                    QueueDel(gridUid);
            }
        }
    }

    private void RemoveOrganizationOutpostGrid(Guid orgId, EntityUid grid)
    {
        if (_outpostStations.Remove(orgId, out var station) && !TerminatingOrDeleted(station))
            QueueDel(station);
        if (!TerminatingOrDeleted(grid))
            QueueDel(grid);
    }

    private void ClearRoundOutpostReferences()
    {
        _pendingBlueprintUploads.Clear();
        _outpostRoundStarted = false;
        // The round manager removes round maps. Do not persist old EntityUid values.
        _spawnedOrganizationOutposts.Clear();
        _outpostStations.Clear();
        _lastOutpostRedeployAttempt.Clear();
        _outpostDeploymentErrors.Clear();
        _factionLocatorNextPing.Clear();
    }

    private void OnOutpostRoundStarting(RoundStartingEvent args)
    {
        _pendingBlueprintUploads.Clear();
        _outpostRoundStarted = true;
        _spawnedOrganizationOutposts.Clear();
        _outpostStations.Clear();
        ClearRoundFleetReferences();
        foreach (var id in _organizations.Keys.OrderBy(x => x).ToArray())
        {
            // Commit can restore organization instances on save failure.
            if (!_organizations.TryGetValue(id, out var org) ||
                org.InsolvencyState == OrganizationInsolvencyState.Liquidation ||
                !IsValidOutpostHash(org.OutpostActiveHash))
                continue;

            // A scheduled replacement becomes eligible after its dismantling delay.
            // Old construction is NEVER deleted in the middle of a played round.
            if (IsValidOutpostHash(org.OutpostReplacementHash) &&
                org.OutpostReplacementHash != org.OutpostActiveHash &&
                org.OutpostReplacementRequestedUtc != default &&
                DateTime.UtcNow >= org.OutpostReplacementRequestedUtc + OutpostReplacementDelay &&
                org.Treasury >= OrganizationOutpostRules.ReplacementPrice &&
                org.CapitalSpent <= long.MaxValue - OrganizationOutpostRules.ReplacementPrice &&
                _store.Available)
            {
                if (TrySpawnOrganizationOutpost(org, org.OutpostReplacementHash, out var next))
                {
                    var newHash = org.OutpostReplacementHash;
                    var newName = org.OutpostReplacementName;
                    if (Commit(() =>
                        {
                            org.Treasury -= OrganizationOutpostRules.ReplacementPrice;
                            org.CapitalSpent += OrganizationOutpostRules.ReplacementPrice;
                            org.OutpostActiveHash = newHash;
                            org.OutpostActiveName = newName;
                            org.OutpostReplacementHash = string.Empty;
                            org.OutpostReplacementName = string.Empty;
                            org.OutpostReplacementRequestedUtc = default;
                        }))
                    {
                        _spawnedOrganizationOutposts[id] = next;
                        _adminLog.Add(LogType.Action, LogImpact.Medium,
                            $"Organization outpost replacement completed: org={id}, hash={newHash}");
                        continue;
                    }

                    RemoveOrganizationOutpostGrid(id, next.Grid);
                }
                _log.GetSawmill("organizations.outposts").Warning(
                    $"Outpost replacement postponed for {id}; retaining previous blueprint.");
                // After failed save, Restore() replaced all Organization objects.
                if (!_organizations.TryGetValue(id, out org))
                    continue;
            }

            if (TrySpawnOrganizationOutpost(org, org.OutpostActiveHash, out var current))
                _spawnedOrganizationOutposts[id] = current;
            else
                _log.GetSawmill("organizations.outposts").Warning(
                    $"Outpost for org={id} is temporarily unavailable; ownership and treasury remain untouched.");
        }
    }

    private bool TryGetOutpostOwner(EntityUid console, EntityUid actor, Guid id,
        out Organization org)
    {
        org = default!;
        if (!AuthorizedBusiness(console, actor) || !_store.Available ||
            !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(id, out var found) ||
            found.Owner != key || found.InsolvencyState != OrganizationInsolvencyState.Active)
            return false;
        org = found;
        return true;
    }

    private void OnPurchaseOutpost(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationPurchaseOutpostMessage args)
    {
        var actor = args.Actor;
        if (!TryGetOutpostOwner(uid, actor, args.Organization, out var org))
            return;
        if (!_outpostRoundStarted)
        {
            Refresh(uid, actor, "Придбати місце можна після початку раунду.");
            return;
        }
        if (IsValidOutpostHash(org.OutpostActiveHash))
        {
            Refresh(uid, actor, "Ця організація вже має місце під аванпост.");
            return;
        }
        if (_organizations.Values.Count(item => IsValidOutpostHash(item.OutpostActiveHash)) >= MaximumPurchasedOutposts)
        {
            Refresh(uid, actor, "У секторі зайнято всі 16 місць під організаційні аванпости.");
            return;
        }
        if (org.Treasury < OrganizationOutpostRules.SitePrice || !IsOutpostBlueprintReady(org))
        {
            Refresh(uid, actor, "Потрібні перевірене креслення та 18 000 000 $ у скарбниці.");
            return;
        }
        if (org.CapitalSpent > long.MaxValue - OrganizationOutpostRules.SitePrice)
        {
            Refresh(uid, actor, "Перевищено ліміт обліку вкладеного капіталу.");
            return;
        }
        if (!TrySpawnOrganizationOutpost(org, org.OutpostBlueprintHash, out var deployed))
        {
            Refresh(uid, actor, "Не вдалося розгорнути станцію в спільному секторі. Кошти не списані.");
            return;
        }

        var name = org.OutpostBlueprintName;
        var hash = org.OutpostBlueprintHash;
        if (!Commit(() =>
            {
                org.Treasury -= OrganizationOutpostRules.SitePrice;
                org.CapitalSpent += OrganizationOutpostRules.SitePrice;
                org.OutpostActiveName = name;
                org.OutpostActiveHash = hash;
                org.OutpostPurchasedUtc = DateTime.UtcNow;
            }))
        {
            RemoveOrganizationOutpostGrid(org.Id, deployed.Grid);
            Refresh(uid, actor, "Реєстр не зміг зберегти придбання. Кошти не списані.");
            return;
        }
        _spawnedOrganizationOutposts[org.Id] = deployed;
        _adminLog.Add(LogType.Action, LogImpact.High,
            $"Organization outpost bought: org={org.Id}, price={OrganizationOutpostRules.SitePrice}, hash={hash}");
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor, "Аванпост зареєстровано як станцію в спільному секторі. Місце оплачено.");
    }

    private void OnScheduleOutpostReplacement(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationScheduleOutpostReplacementMessage args)
    {
        var actor = args.Actor;
        if (!TryGetOutpostOwner(uid, actor, args.Organization, out var org))
            return;
        if (!IsValidOutpostHash(org.OutpostActiveHash) || !IsOutpostBlueprintReady(org) ||
            org.OutpostActiveHash == org.OutpostBlueprintHash)
        {
            Refresh(uid, actor, "Потрібно підготувати інше перевірене креслення.");
            return;
        }
        if (org.OutpostReplacementHash.Length > 0)
        {
            Refresh(uid, actor, "Спочатку скасуйте попередню заміну.");
            return;
        }
        if (org.Treasury < OrganizationOutpostRules.ReplacementPrice)
        {
            Refresh(uid, actor, "Для заміни потрібно щонайменше 5 000 000 $ у скарбниці.");
            return;
        }
        var name = org.OutpostBlueprintName;
        var hash = org.OutpostBlueprintHash;
        if (!Commit(() =>
            {
                org.OutpostReplacementHash = hash;
                org.OutpostReplacementName = name;
                org.OutpostReplacementRequestedUtc = DateTime.UtcNow;
            }))
        {
            Refresh(uid, actor, "Не вдалося записати заявку на заміну.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Outpost replacement scheduled: org={org.Id}, hash={hash}");
        Refresh(uid, actor,
            "Заміну заплановано. Після 30 хвилин демонтажу нова станція завантажиться з початком наступного раунду. Оплата лише після успіху.");
    }

    private void OnCancelOutpostReplacement(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationCancelOutpostReplacementMessage args)
    {
        var actor = args.Actor;
        if (!TryGetOutpostOwner(uid, actor, args.Organization, out var org) ||
            org.OutpostReplacementHash.Length == 0)
            return;
        if (!Commit(() =>
            {
                org.OutpostReplacementHash = string.Empty;
                org.OutpostReplacementName = string.Empty;
                org.OutpostReplacementRequestedUtc = default;
            }))
            return;
        Refresh(uid, actor, "Заміну скасовано. Поточний аванпост залишається.");
    }

    private void CleanupOrphanedOrganizationOutposts()
    {
        foreach (var (id, deployment) in _spawnedOrganizationOutposts.ToArray())
        {
            if (_organizations.TryGetValue(id, out var organization) &&
                organization.InsolvencyState != OrganizationInsolvencyState.Liquidation)
                continue;
            RemoveOrganizationOutpostGrid(id, deployment.Grid);
            _spawnedOrganizationOutposts.Remove(id);
        }
    }

    private void OnRedeployOutpost(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationRedeployOutpostMessage args)
    {
        var actor = args.Actor;
        if (!TryGetOutpostOwner(uid, actor, args.Organization, out var org))
            return;
        if (!_outpostRoundStarted || !IsValidOutpostHash(org.OutpostActiveHash))
            return;
        if (IsOutpostPhysicallyPresent(org.Id))
        {
            Refresh(uid, actor, "Аванпост уже розгорнуто в цьому раунді.");
            return;
        }
        if (_lastOutpostRedeployAttempt.TryGetValue(org.Id, out var last) &&
            _gameTiming.CurTime - last < TimeSpan.FromSeconds(30))
        {
            Refresh(uid, actor, "Повторна спроба доступна через 30 секунд.");
            return;
        }
        _lastOutpostRedeployAttempt[org.Id] = _gameTiming.CurTime;
        if (!TrySpawnOrganizationOutpost(org, org.OutpostActiveHash, out var deployed))
        {
            Refresh(uid, actor, "Повторне розгортання не вдалося. Скарбниця не змінена.");
            return;
        }
        _spawnedOrganizationOutposts[org.Id] = deployed;
        Refresh(uid, actor, "Аванпост повторно розгорнуто в спільному секторі.");
    }

}
