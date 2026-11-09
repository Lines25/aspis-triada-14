using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Enums;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Reconciles ship IFF with the currently controlled character's membership.
/// A NetUserId identifies the purchased vessel, but membership belongs to the
/// player's spawned CHARACTER, not the account or an arbitrary PDA body.
/// </summary>
public sealed partial class OrganizationSystem
{
    private readonly Dictionary<EntityUid, Color> _unaffiliatedShipColors = new();

    private void RefreshOrganizationShipIFF()
    {
        // Query only actual owned grids. In particular, never recolor POI or
        // commercial station grids merely because a faction member is aboard.
        var owned = EntityQueryEnumerator<ShipOwnershipComponent>();
        while (owned.MoveNext(out var grid, out var ownership))
        {
            if (TerminatingOrDeleted(grid))
                continue;
            Organization? organization = null;
            var ownerResolved = false;
            foreach (var session in _players.Sessions)
            {
                if (session.UserId != ownership.OwnerUserId ||
                    session.Status != SessionStatus.InGame ||
                    session.AttachedEntity is not { Valid: true } actor ||
                    !TryGetCharacter(actor, out var character, out _) ||
                    !IsOriginalController(actor))
                    continue;
                organization = FindMembership(character);
                ownerResolved = true;
                break;
            }
            // An offline captain's affiliation does not vanish from IFF.
            // Only an authenticated, active character can change/remove it.
            if (ownerResolved)
                ApplyOrganizationShipIFF(grid, organization);
        }

        // Corporate rental grids are organization assets, not the renter's
        // personal ships. They use the leasing company's registered color/name.
        foreach (var (organizationId, issued) in _issuedFleetShuttles)
        {
            if (TerminatingOrDeleted(issued.Grid))
                continue;
            _organizations.TryGetValue(organizationId, out var organization);
            ApplyOrganizationShipIFF(issued.Grid, organization);
        }
    }

    private void ApplyOrganizationShipIFF(EntityUid grid, Organization? organization)
    {
        if (TryComp<IFFComponent>(grid, out var existing) && existing.ReadOnly)
            return; // Never override protected station/POI IFF.

        if (organization == null)
        {
            if (!TryComp<OrganizationShipIFFComponent>(grid, out _))
                return;
            RemComp<OrganizationShipIFFComponent>(grid);
            if (_unaffiliatedShipColors.Remove(grid, out var previous))
                _organizationShuttle.SetIFFColor(grid, previous);
            return;
        }

        if (!TryComp<OrganizationShipIFFComponent>(grid, out var affiliation))
        {
            _unaffiliatedShipColors.TryAdd(grid,
                existing?.Color ?? IFFComponent.IFFColor);
            affiliation = EnsureComp<OrganizationShipIFFComponent>(grid);
        }
        var id = organization.Id.ToString("N");
        if (affiliation.OrganizationId != id || affiliation.OrganizationName != organization.Name)
        {
            affiliation.OrganizationId = id;
            affiliation.OrganizationName = organization.Name;
            Dirty(grid, affiliation);
        }

        // The current IFF component is networked and drives the ordinary
        // shuttle radar and scanning-console color without custom UI hacks.
        _organizationShuttle.SetIFFColor(grid, Color.FromHex(organization.Color));
    }
}
