using System;
using System.Linq;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private void OnUpdateDescription(EntityUid uid, OrganizationConsoleComponent component,
        OrganizationUpdateDescriptionMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Settings,
                out var actor, out var org))
            return;
        var description = args.Description?.Trim() ?? string.Empty;
        if (description.Length > 500 ||
            description.Any(ch => char.IsControl(ch) && ch != '\n' && ch != '\r'))
        {
            Refresh(uid, actor, "Опис не може бути довшим за 500 символів.");
            return;
        }
        if (!Commit(() => org.Description = description))
        {
            Refresh(uid, actor, "Не вдалося зберегти опис.");
            return;
        }
        Refresh(uid, actor, "Опис організації оновлено.");
    }

    private void OnClearFleetListing(EntityUid uid, OrganizationConsoleComponent component,
        OrganizationClearFleetListingMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            !_store.Available || _fleetUsedThisRound.Contains(org.Id))
            return;
        if (!Commit(() =>
            {
                org.FleetListingName = string.Empty;
                org.FleetApprovedBlueprintHash = string.Empty;
                org.FleetListingDescription = string.Empty;
                org.FleetRentalPrice = OrganizationFleetRules.DefaultRentalPrice;
            }))
        {
            Refresh(uid, actor, "Не вдалося стерти умови оренди.");
            return;
        }
        Refresh(uid, actor, "Умови оренди стерто. Креслення залишилося збереженим.");
    }
}
