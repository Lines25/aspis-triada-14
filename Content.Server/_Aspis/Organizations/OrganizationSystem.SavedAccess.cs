using System;
using System.Linq;
using Content.Shared.Access.Components;
using Content.Shared.Doors.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared._Triad.Shipyard.Save;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private void OnCorporateSavedShipLoaded(ShipyardShuttleLoadEvent args)
    {
        var orgId = Guid.Empty;
        if (IsOriginalController(args.Purchaser) &&
            TryGetCharacter(args.Purchaser, out var key, out _) &&
            FindMembership(key) is { } org && org.Owner == key)
            orgId = org.Id;

        RestoreSavedCorporateReaders(orgId, args.Shuttle);
    }

    // The server determines the owning organization; a saved GUID cannot grant ownership.
    // Older saves did not serialize the GUID, but retained the slot/mask.
    private void RestoreSavedCorporateReaders(Guid orgId, EntityUid grid)
    {
        _organizations.TryGetValue(orgId, out var org);
        var purchased = 0;
        if (org != null)
            foreach (var profile in org.AccessProfiles)
                if (profile.Slot is >= 1 and <= MaxOrganizationAccessSlots)
                    purchased |= 1 << (profile.Slot - 1);

        var query = EntityQueryEnumerator<OrganizationPropertyAccessReaderComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var reader, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            var mask = GetCorporateDoorMask(uid, reader);
            // Do not remove an invalid restriction and accidentally unlock the door.
            reader.Organization = org != null && mask != 0 && (mask & purchased) == mask &&
                HasComp<DoorComponent>(uid) && HasComp<AccessReaderComponent>(uid) &&
                (reader.Organization == Guid.Empty || reader.Organization == orgId)
                ? orgId : Guid.Empty;
            Dirty(uid, reader);
        }
    }
}
