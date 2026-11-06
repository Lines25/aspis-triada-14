using System;
using System.Linq;
using System.Collections.Generic;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Components;
using Content.Shared.Physics;
using System.Numerics;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Interaction;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Server.Player;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private const long OutpostAccessConfiguratorPrice = 30_000;
    private const long OutpostBeaconPrice = 2_000;

    [Dependency] private StationSpawningSystem _organizationSpawn = default!;
    // GetTileMixture reads the authoritative server atmosphere on the beacon
    // tile. Never use a client's cached gas state to authorize respawning.
    [Dependency] private AtmosphereSystem _organizationOutpostAtmos = default!;
    [Dependency] private Content.Shared.Interaction.SharedInteractionSystem _organizationOutpostInteraction = default!;

    private bool TryGetOutpostMember(Guid organizationId, CharacterKey character,
        out Organization org, out OrganizationOutpostAccessTier tier)
    {
        tier = OrganizationOutpostAccessTier.Visitor;
        org = default!;
        if (!_organizations.TryGetValue(organizationId, out var organization) ||
            organization.Kind != OrganizationKind.Company ||
            organization.InsolvencyState != OrganizationInsolvencyState.Active ||
            !organization.Members.TryGetValue(character, out var member))
            return false;

        org = organization;
        tier = member.Role == OrganizationMemberRole.Owner
            ? OrganizationOutpostAccessTier.Owner
            : member.Id == organization.DeputyMemberId
                ? OrganizationOutpostAccessTier.Deputy
                : OrganizationOutpostAccessTier.Member;
        return true;
    }

    private void OnOrganizationReaderCheck(EntityUid uid, OrganizationOutpostDoorComponent reader,
        OrganizationOutpostAccessCheckEvent args)
    {
        args.Authorized = TryGetCharacter(args.User, out var key, out _) &&
            IsOriginalController(args.User) &&
            TryGetOutpostMember(reader.Organization, key, out _, out var tier) &&
            tier >= reader.RequiredTier;
    }

    private bool IsOnOwnedOutpost(EntityUid target, Guid orgId)
    {
        return _spawnedOrganizationOutposts.TryGetValue(orgId, out var outpost) &&
               !TerminatingOrDeleted(outpost.Grid) &&
               !TerminatingOrDeleted(target) &&
               Transform(target).GridUid == outpost.Grid;
    }

    /// <summary>Authoritative gate for the corporate mode of the stock access configurator.</summary>
    public bool IsOrganizationConfiguratorTarget(EntityUid token, EntityUid target)
    {
        return TryComp<OrganizationOutpostConfiguratorComponent>(token, out var tokenComp) &&
               !TerminatingOrDeleted(target) &&
               HasComp<AccessReaderComponent>(target) &&
               IsOnOwnedOutpost(target, tokenComp.Organization);
    }

    public bool ConfigureOrganizationOutpostDoor(EntityUid token, EntityUid user,
        EntityUid target, OrganizationOutpostAccessTier tier)
    {
        if (tier is < OrganizationOutpostAccessTier.Member or > OrganizationOutpostAccessTier.Owner ||
            !IsOrganizationConfiguratorTarget(token, target) ||
            !TryComp<OrganizationOutpostConfiguratorComponent>(token, out var card) ||
            !IsOriginalController(user) ||
            !_organizationOutpostInteraction.InRangeUnobstructed(user, target) ||
            !TryGetCharacter(user, out var key, out _) ||
            !TryGetOutpostMember(card.Organization, key, out _, out var role) ||
            role != OrganizationOutpostAccessTier.Owner)
            return false;

        var access = EnsureComp<OrganizationOutpostDoorComponent>(target);
        access.Organization = card.Organization;
        access.RequiredTier = tier;
        Dirty(target, access);
        _quotaPopup.PopupEntity($"Корпоративний доступ: {tier}.", user, user);
        return true;
    }

    private void OnOutpostConfiguratorUse(EntityUid token, OrganizationOutpostConfiguratorComponent comp,
        AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target ||
            !HasComp<AccessReaderComponent>(target) ||
            !IsOnOwnedOutpost(target, comp.Organization) ||
            !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var key, out _) ||
            !TryGetOutpostMember(comp.Organization, key, out _, out var tier) ||
            tier != OrganizationOutpostAccessTier.Owner)
            return;

        var access = EnsureComp<OrganizationOutpostDoorComponent>(target);
        access.Organization = comp.Organization;
        access.RequiredTier = comp.SelectedTier;
        Dirty(target, access);
        args.Handled = true;
        _quotaPopup.PopupEntity($"Корпоративний доступ: {comp.SelectedTier}. Діє лише для вашої компанії.",
            args.User, args.User);
    }

    private void OnOutpostConfiguratorCycle(EntityUid token, OrganizationOutpostConfiguratorComponent comp,
        InteractHandEvent args)
    {
        if (args.Handled || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var key, out _) ||
            !TryGetOutpostMember(comp.Organization, key, out _, out var tier) ||
            tier != OrganizationOutpostAccessTier.Owner)
            return;
        comp.SelectedTier = comp.SelectedTier switch
        {
            OrganizationOutpostAccessTier.Member => OrganizationOutpostAccessTier.Deputy,
            OrganizationOutpostAccessTier.Deputy => OrganizationOutpostAccessTier.Owner,
            _ => OrganizationOutpostAccessTier.Member
        };
        Dirty(token, comp);
        args.Handled = true;
        _quotaPopup.PopupEntity($"Рівень конфігуратора: {comp.SelectedTier}.", args.User, args.User);
    }

    private void OnConfigureOutpostListing(EntityUid console, OrganizationConsoleComponent component,
        OrganizationConfigureOutpostListingMessage args)
    {
        if (!TryGetOutpostOwner(console, args.Actor, args.Organization, out var org) ||
            !IsValidOutpostHash(org.OutpostActiveHash))
            return;
        var name = args.Name?.Trim() ?? string.Empty;
        var description = args.Description?.Trim() ?? string.Empty;
        if (name.Length is < 3 or > 52 || description.Length > 200 ||
            name.Any(char.IsControl) || description.Any(char.IsControl))
        {
            Refresh(console, args.Actor, "Назва: 3–52 знаки; опис: до 200, без службових символів.");
            return;
        }
        if (!Commit(() =>
        {
            org.OutpostPublicName = name;
            org.OutpostPublicDescription = description;
            org.OutpostAllowVisitors = args.AllowVisitors;
        }))
        {
            Refresh(console, args.Actor, "Не вдалося зберегти інформацію про станцію.");
            return;
        }
        if (_outpostStations.TryGetValue(org.Id, out var station) && !TerminatingOrDeleted(station))
            _organizationStationSystem.RenameStation(station, OutpostStationTitle(org), false);
        if (_spawnedOrganizationOutposts.TryGetValue(org.Id, out var location) &&
            !TerminatingOrDeleted(location.Grid))
            _organizationMetadata.SetEntityName(location.Grid, OutpostStationTitle(org));
        Refresh(console, args.Actor, "Назву, опис та доступність станції оновлено.");
    }

    private static string OutpostStationTitle(Organization org) =>
        string.IsNullOrWhiteSpace(org.OutpostPublicName)
            ? $"{org.Name} | аванпост" : org.OutpostPublicName;

    private void OnPrintOutpostTool(EntityUid console, OrganizationConsoleComponent component,
        OrganizationPrintOutpostToolMessage args)
    {
        if (!TryGetOutpostOwner(console, args.Actor, args.Organization, out var org) ||
            org.Kind != OrganizationKind.Company ||
            !Enum.IsDefined(args.Tier))
            return;
        if (!IsValidOutpostHash(org.OutpostActiveHash))
        {
            Refresh(console, args.Actor, "Спочатку потрібно зареєструвати корпоративний аванпост.");
            return;
        }
        var price = args.Configurator ? OutpostAccessConfiguratorPrice : OutpostBeaconPrice;
        if (org.Treasury < price || org.CapitalSpent > long.MaxValue - price)
        {
            Refresh(console, args.Actor, "Недостатньо коштів у скарбниці.");
            return;
        }
        var proto = args.Configurator ? "AspisOutpostAccessConfigurator" : "AspisOutpostSpawnBeacon";
        if (!_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(proto))
        {
            Refresh(console, args.Actor, "Прототип обладнання аванпосту не завантажений.");
            return;
        }
        EntityUid item;
        try { item = Spawn(proto, Transform(args.Actor).Coordinates); }
        catch (Exception ex)
        {
            _log.GetSawmill("organizations.outposts").Error($"Cannot print outpost tool: {ex}");
            Refresh(console, args.Actor, "Не вдалося видати обладнання.");
            return;
        }
        if (!Commit(() =>
            {
                org.Treasury -= price;
                org.CapitalSpent += price;
            }))
        {
            QueueDel(item);
            Refresh(console, args.Actor, "Не вдалося зберегти оплату. Обладнання не видано.");
            return;
        }
        if (args.Configurator)
        {
            var token = EnsureComp<OrganizationOutpostConfiguratorComponent>(item);
            token.Organization = org.Id;
            token.SelectedTier = OrganizationOutpostAccessTier.Member;
            Dirty(item, token);
        }
        else
        {
            var beacon = EnsureComp<OrganizationOutpostSpawnBeaconComponent>(item);
            beacon.Organization = org.Id;
            beacon.Tier = args.Tier;
            beacon.Validated = false;
            Dirty(item, beacon);
        }
        Refresh(console, args.Actor, args.Configurator
            ? "Карту-конфігуратор видано. Вона не є ID-карткою та не видає станційних доступів."
            : "Маяк видано. Розмістіть на підлозі аванпосту й активуйте. Для появи у наступних раундах додайте маяк до підписаного креслення.");
    }

    /// <summary>
    /// A spawn point is not safe merely because it has a solid floor.
    /// Validate the actual server-side atmosphere each time a player requests
    /// to spawn; a once-validated beacon may become depressurized later.
    /// Conservative limits intentionally reject unknown/unsimulated atmos.
    /// </summary>
    private bool IsOutpostSpawnAtmosphereSafe(EntityUid gridUid, (int X, int Y) tile)
    {
        var air = _organizationOutpostAtmos.GetTileMixture(
            gridUid, null, new Robust.Shared.Maths.Vector2i(tile.X, tile.Y));
        if (!_organizationOutpostAtmos.IsMixtureProbablySafe(air) ||
            air is null || air.TotalMoles <= 0f ||
            air.Temperature < 273f || air.Temperature > 323f)
            return false;

        // The default "probably safe" check intentionally does not inspect
        // oxygen. These are unsuited players, so require breathable partial
        // oxygen pressure rather than just total gas pressure.
        var oxygenPressure = air.Pressure * air.GetMoles(Gas.Oxygen) / air.TotalMoles;
        return float.IsFinite(oxygenPressure) && oxygenPressure >= 16_000f &&
               oxygenPressure <= 40_000f;
    }

    private bool IsValidOutpostBeacon(EntityUid beaconUid, OrganizationOutpostSpawnBeaconComponent beacon,
        EntityUid gridUid, bool requireAtmosphere = true)
    {
        if (TerminatingOrDeleted(beaconUid) || Transform(beaconUid).GridUid != gridUid ||
            Transform(beaconUid).ParentUid != gridUid ||
            (beacon.Validated && Vector2.DistanceSquared(
                Transform(beaconUid).Coordinates.Position, beacon.ValidatedLocalPosition) > 0.16f) ||
            !TryComp<MapGridComponent>(gridUid, out var mapGrid))
            return false;
        var local = Transform(beaconUid).Coordinates.Position;
        var tile = ((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y));
        var tileRef = _organizationMapSystem.GetTileRef(gridUid, mapGrid,
            new Robust.Shared.Maths.Vector2i(tile.Item1, tile.Item2));
        if (tileRef.Tile.IsEmpty || !WreckTileClear(gridUid, mapGrid, tile) ||
            (requireAtmosphere && !IsOutpostSpawnAtmosphereSafe(gridUid, tile)))
            return false;
        // Avoid one-cell gaps. This is a conservative clearance check, not a
        // guarantee that an arbitrary user-made station has a breathable route.
        var free = 0;
        foreach (var offset in new[] { (1,0), (-1,0), (0,1), (0,-1) })
        {
            var neighbor = (tile.Item1 + offset.Item1, tile.Item2 + offset.Item2);
            if (!_organizationMapSystem.GetTileRef(gridUid, mapGrid,
                    new Robust.Shared.Maths.Vector2i(neighbor.Item1, neighbor.Item2)).Tile.IsEmpty &&
                WreckTileClear(gridUid, mapGrid, neighbor))
                free++;
        }
        // Public spawns must have an unobstructed route to at least one
        // *actual docking airlock*. A floor-only check is not sufficient.
        return free >= 2 &&
               (beacon.Tier != OrganizationOutpostAccessTier.Visitor ||
                HasPublicVisitorRoute(gridUid, mapGrid, tile));
    }

    private bool HasPublicVisitorRoute(EntityUid gridUid, MapGridComponent grid,
        (int X, int Y) beaconTile)
    {
        var destinations = new HashSet<(int X, int Y)>();
        var docks = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (docks.MoveNext(out var uid, out var dock, out var xform))
        {
            if (xform.GridUid != gridUid || (dock.DockType & DockType.Airlock) == 0 ||
                HasComp<OrganizationOutpostDoorComponent>(uid))
                continue;
            if (TryComp<AccessReaderComponent>(uid, out var dockReader) && dockReader.Enabled &&
                (dockReader.AccessLists.Count > 0 || dockReader.AccessKeys.Count > 0))
                continue;

            var dockTile = ((int)MathF.Floor(xform.Coordinates.Position.X),
                            (int)MathF.Floor(xform.Coordinates.Position.Y));
            // Dock entities normally occupy their own tile. Find a walkable
            // interior-adjacent tile rather than requiring the dock to be walkable.
            foreach (var step in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var candidate = (dockTile.Item1 + step.Item1, dockTile.Item2 + step.Item2);
                if (VisitorWalkableTile(gridUid, grid, candidate))
                    destinations.Add(candidate);
            }
        }

        if (destinations.Count == 0)
            return false;

        // Conservative access audit: no staff doors or other restricted readers
        // along the public route. Never use generic spawn points as fallback.
        var seen = new HashSet<(int X, int Y)> { beaconTile };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue(beaconTile);
        const int maxWalkTiles = 8_192;
        while (queue.Count > 0 && seen.Count <= maxWalkTiles)
        {
            var point = queue.Dequeue();
            if (destinations.Contains(point))
                return true;

            foreach (var step in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var next = (point.X + step.Item1, point.Y + step.Item2);
                if (!seen.Add(next) || !VisitorWalkableTile(gridUid, grid, next))
                    continue;
                queue.Enqueue(next);
            }
        }
        return false;
    }

    private bool VisitorWalkableTile(EntityUid gridUid, MapGridComponent grid,
        (int X, int Y) tile)
    {
        var tileRef = _organizationMapSystem.GetTileRef(gridUid, grid,
            new Robust.Shared.Maths.Vector2i(tile.X, tile.Y));
        if (tileRef.Tile.IsEmpty || !WreckTileClear(gridUid, grid, tile))
            return false;

        // An open restricted airlock must not temporarily make a private
        // corridor eligible for visitor spawning.
        var lookup = EntityManager.System<EntityLookupSystem>();
        foreach (var entity in lookup.GetLocalEntitiesIntersecting(tileRef,
                     flags: LookupFlags.Dynamic | LookupFlags.Static))
        {
            if (HasComp<OrganizationOutpostDoorComponent>(entity))
                return false;
            if (TryComp<AccessReaderComponent>(entity, out var reader) && reader.Enabled &&
                (reader.AccessLists.Count > 0 || reader.AccessKeys.Count > 0))
                return false;
        }
        return true;
    }

    private void OnOutpostBeaconActivate(EntityUid uid, OrganizationOutpostSpawnBeaconComponent beacon,
        InteractHandEvent args)
    {
        if (args.Handled || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var key, out _) ||
            !TryGetOutpostMember(beacon.Organization, key, out _, out var role) ||
            role != OrganizationOutpostAccessTier.Owner ||
            !_spawnedOrganizationOutposts.TryGetValue(beacon.Organization, out var outpost) ||
            !IsValidOutpostBeacon(uid, beacon, outpost.Grid))
        {
            _quotaPopup.PopupEntity("Маяк має бути на безпечній підлозі власного аванпосту: потрібні вільний прохід, нормальний тиск, кисень та температура. Активацію виконує власник.", args.User, args.User);
            return;
        }
        beacon.ValidatedLocalPosition = Transform(uid).Coordinates.Position;
        beacon.Validated = true;
        Dirty(uid, beacon);
        EnableCurrentOutpostLateJoin(beacon.Organization, outpost.Grid);
        args.Handled = true;
        _quotaPopup.PopupEntity($"Маяк появи {beacon.Tier} перевірено для цього раунду.", args.User, args.User);
    }

    private OrganizationOutpostSpawnBeaconComponent? FindOutpostBeacon(Guid orgId, EntityUid grid,
        OrganizationOutpostAccessTier tier, out EntityUid beaconUid)
    {
        beaconUid = default;
        var query = EntityQueryEnumerator<OrganizationOutpostSpawnBeaconComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Organization != orgId || marker.Tier != tier || !marker.Validated ||
                !IsValidOutpostBeacon(uid, marker, grid))
                continue;
            beaconUid = uid;
            return marker;
        }
        return null;
    }

    private bool TryGetOutpostSpawn(ICommonSession session, HumanoidCharacterProfile profile, EntityUid station,
        out EntityUid markerUid)
    {
        markerUid = default;
        if (!TryComp<OrganizationOutpostStationComponent>(station, out var stationMarker) ||
            !_spawnedOrganizationOutposts.TryGetValue(stationMarker.OrganizationId, out var outpost) ||
            !_preferences.TryGetCachedPreferences(session.UserId, out var prefs))
            return false;
        var key = Key(session.UserId.ToString(), prefs.SelectedCharacterIndex, profile.Name);
        // Visitors must use the separately audited Visitor beacon. Never
        // fall back to another corporate role for non-members.
        var member = TryGetOutpostMember(stationMarker.OrganizationId, key, out _, out var memberTier);
        if (!member && (!_organizations.TryGetValue(stationMarker.OrganizationId, out var host) ||
                        !host.OutpostAllowVisitors))
            return false;
        var level = member ? memberTier : OrganizationOutpostAccessTier.Visitor;
        // The holder's role controls which beacon is considered; never fall
        // back to a higher-privilege role or an unrelated generic spawnpoint.
        for (var tier = (int)level; tier >= (int)OrganizationOutpostAccessTier.Visitor; tier--)
        {
            if (FindOutpostBeacon(stationMarker.OrganizationId, outpost.Grid,
                    (OrganizationOutpostAccessTier)tier, out markerUid) != null)
                return true;
        }
        return false;
    }

    public bool CanJoinOrganizationOutpost(EntityUid station, ICommonSession session,
        HumanoidCharacterProfile profile)
    {
        if (!HasComp<OrganizationOutpostStationComponent>(station))
            return true;
        return TryGetOutpostSpawn(session, profile, station, out _);
    }

    /// <returns>True if this is a corporate station, even if it denied spawning.</returns>
    public bool TrySpawnOnOrganizationOutpost(PlayerSpawningEvent args)
    {
        if (args.Station is not { } station || !HasComp<OrganizationOutpostStationComponent>(station))
            return false;
        if (args.Session is not { } session || args.HumanoidCharacterProfile is not { } profile ||
            !TryGetOutpostSpawn(session, profile, station, out var markerUid))
            return true;
        args.SpawnResult = _organizationSpawn.SpawnPlayerMob(
            Transform(markerUid).Coordinates, args.Job, profile, station, session: session);
        return true;
    }

    private void EnableCurrentOutpostLateJoin(Guid organizationId, EntityUid grid)
    {
        if (!_outpostStations.TryGetValue(organizationId, out var original) ||
            TerminatingOrDeleted(original) ||
            HasComp<Content.Server.Station.Components.StationJobsComponent>(original))
            return;

        // A deployed station with a newly installed beacon needs a fresh
        // station registration to expose job selection. Keep the same grid.
        EntityUid? replacement = null;
        try
        {
            replacement = Spawn("AspisOrganizationOutpostJoinableStation",
                Robust.Shared.Map.MapCoordinates.Nullspace);
            _organizationStationSystem.RenameStation(replacement.Value, Name(original), false);
            EnsureComp<OrganizationOutpostStationComponent>(replacement.Value).OrganizationId = organizationId;
            _organizationStationSystem.RemoveGridFromStation(original, grid);
            _organizationStationSystem.AddGridToStation(replacement.Value, grid);
            _outpostStations[organizationId] = replacement.Value;
            QueueDel(original);
        }
        catch (Exception ex)
        {
            _log.GetSawmill("organizations.outposts").Error(
                $"Could not enable corporate late-join for {organizationId}: {ex}");
            if (replacement is { } created && !TerminatingOrDeleted(created))
            {
                // StationData.Grids belongs to StationSystem (RA0002). Query
                // membership through its public API instead of invoking the
                // HashSet stored in another system's component.
                if (!TerminatingOrDeleted(grid) &&
                    _organizationStationSystem.GetOwningStation(grid) == created)
                    _organizationStationSystem.RemoveGridFromStation(created, grid);
                QueueDel(created);
            }
            if (!TerminatingOrDeleted(original) && !TerminatingOrDeleted(grid) &&
                _organizationStationSystem.GetOwningStation(grid) != original)
                _organizationStationSystem.AddGridToStation(original, grid);
        }
    }

    private void RestoreOutpostCompanySecurity(Guid orgId, EntityUid grid)
    {
        // The signed blueprint is a structural snapshot, NOT an authority
        // source for which company currently owns it. Rebind saved corporate
        // access settings to the active owner at every deployment.
        var invalidDoors = new List<EntityUid>();
        var doors = EntityQueryEnumerator<OrganizationOutpostDoorComponent, TransformComponent>();
        while (doors.MoveNext(out var uid, out var door, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            // A bogus corporate reader in a ship YAML must not add rights to
            // a device that cannot enforce AccessReader restrictions.
            if (!HasComp<AccessReaderComponent>(uid) ||
                door.RequiredTier is < OrganizationOutpostAccessTier.Member or > OrganizationOutpostAccessTier.Owner)
            {
                invalidDoors.Add(uid);
                continue;
            }

            door.Organization = orgId;
            Dirty(uid, door);
        }
        // Modifying the component store during EntityQueryEnumerator iteration
        // can invalidate the enumerator. Apply removals after the walk.
        foreach (var uid in invalidDoors)
            RemComp<OrganizationOutpostDoorComponent>(uid);

        // Prevent reproducing paid configuration tokens for free by saving
        // their entity in a ship blueprint and then redeploying that file.
        var tokens = EntityQueryEnumerator<OrganizationOutpostConfiguratorComponent, TransformComponent>();
        while (tokens.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == grid)
                QueueDel(uid);
        }
    }

    private bool TryBindOutpostSpawnBeacons(Guid orgId, EntityUid grid)
    {
        RestoreOutpostCompanySecurity(orgId, grid);
        var valid = false;
        var query = EntityQueryEnumerator<OrganizationOutpostSpawnBeaconComponent>();
        while (query.MoveNext(out var uid, out var beacon))
        {
            if (Transform(uid).GridUid != grid)
                continue;
            // Ignore forged organization IDs in saved ship files.
            beacon.Organization = orgId;
            beacon.Validated = false;
            if (!Enum.IsDefined(beacon.Tier))
            {
                Dirty(uid, beacon);
                continue;
            }
            // A blueprint may load before its grid atmosphere has been
            // simulated. Geometry is validated now, atmosphere is rechecked
            // by FindOutpostBeacon immediately before every actual spawn.
            beacon.Validated = IsValidOutpostBeacon(uid, beacon, grid, requireAtmosphere: false);
            if (beacon.Validated)
                beacon.ValidatedLocalPosition = Transform(uid).Coordinates.Position;
            Dirty(uid, beacon);
            valid |= beacon.Validated;
        }
        return valid;
    }
}
