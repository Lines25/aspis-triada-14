using System;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Damage.Components;
using Content.Shared.Construction.Components;
using Content.Server.Construction.Components;
using Content.Shared.UserInterface;
using Robust.Shared.Map;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private const string FrontierTerminalProto = "AspisFrontierReceivingTerminal";
    private const float FrontierReceiverRadius = 1.5f;
    // Mirrored booths, directly inside the east and west cargo airlocks.
    private static readonly System.Numerics.Vector2 FrontierTerminalRight = new(10.5f, -4.5f);
    private static readonly System.Numerics.Vector2 FrontierTerminalLeft = new(0.5f, -4.5f);

    private bool TryCreateFrontierReceiver(FrontierRouteRecord route, out string failure)
    {
        failure = string.Empty;
        if (!_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(FrontierTerminalProto))
        {
            failure = "Відсутній прототип термінала далекого рубежу.";
            return false;
        }

        var right = Spawn(FrontierTerminalProto,
            new EntityCoordinates(route.Outpost, FrontierTerminalRight));
        var left = Spawn(FrontierTerminalProto,
            new EntityCoordinates(route.Outpost, FrontierTerminalLeft));
        if (!TryComp<OrganizationFrontierReceivingComponent>(right, out var rightData) ||
            !TryComp<OrganizationFrontierReceivingComponent>(left, out var leftData))
        {
            QueueDel(right);
            QueueDel(left);
            failure = "Не вдалося створити обидва приймальні термінали.";
            return false;
        }
        // Face one another across the station, instead of facing south.
        _organizationOutpostTransforms.SetLocalRotation(right, new Angle(MathF.PI / 2f));
        _organizationOutpostTransforms.SetLocalRotation(left, new Angle(-MathF.PI / 2f));
        // These terminals are permanent fittings of the protected station.
        // They cannot be destroyed or unbuilt to bypass the receiving manifest.
        EnsureComp<GodmodeComponent>(right);
        EnsureComp<GodmodeComponent>(left);
        RemComp<AnchorableComponent>(right);
        RemComp<AnchorableComponent>(left);
        RemComp<ConstructionComponent>(right);
        RemComp<ConstructionComponent>(left);
        rightData.Assignment = route.Assignment;
        leftData.Assignment = route.Assignment;
        route.Terminal = right;
        route.SecondTerminal = left;
        RefreshFrontierReceiver(route);
        return true;
    }

    private bool IsFrontierReceiver(FrontierRouteRecord route, EntityUid uid) =>
        route.Terminal == uid || route.SecondTerminal == uid;

    private void OnFrontierReceiverOpened(EntityUid uid, OrganizationFrontierReceivingComponent comp,
        BoundUIOpenedEvent args)
    {
        if (_frontierRoutes.TryGetValue(comp.Assignment, out var route) && IsFrontierReceiver(route, uid))
            RefreshFrontierReceiver(route);
    }

    private void OnFrontierReceiverSelected(EntityUid uid, OrganizationFrontierReceivingComponent comp,
        OrganizationFrontierSelectPostMessage args)
    {
        if (args.Post is < 0 or > 3 ||
            !_frontierRoutes.TryGetValue(comp.Assignment, out var route) ||
            !IsFrontierReceiver(route, uid) || route.Phase != FrontierPhase.Destination ||
            TerminatingOrDeleted(route.Ship) || !IsOriginalController(args.Actor) ||
            !TryGetCharacter(args.Actor, out var character, out _) ||
            FindMembership(character) is not { Kind: OrganizationKind.Company } org ||
            org.Id != route.Organization || Transform(args.Actor).GridUid != route.Outpost ||
            System.Numerics.Vector2.DistanceSquared(Transform(args.Actor).WorldPosition,
                Transform(uid).WorldPosition) > 8f * 8f)
            return;
        comp.SelectedPost = args.Post;
        _audio.PlayPvs(FrontierNavigationCue, uid);
        RefreshFrontierReceiver(route);
    }

    private void RefreshFrontierReceiver(FrontierRouteRecord route)
    {
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.FactionAssignmentId == route.Assignment);
        if (claim == null)
            return;

        foreach (var terminal in new[] { route.Terminal, route.SecondTerminal })
        {
            if (terminal is not { } uid || TerminatingOrDeleted(uid) ||
                !TryComp<OrganizationFrontierReceivingComponent>(uid, out var comp))
                continue;
            var targets = (int[])claim.PostTargets.Clone();
            var delivered = (int[])claim.DeliveredByPost.Clone();
            _ui.SetUiState(uid, OrganizationFrontierReceivingUiKey.Key,
                new OrganizationFrontierReceivingState(comp.SelectedPost, targets, delivered, route.Code));
        }
    }
}
