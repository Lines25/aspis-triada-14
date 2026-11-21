using System.Collections.Generic;
using Content.Server.Construction.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Damage.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Trusted, non-player-editable station infrastructure. ONLY put this marker
/// on the loaded construction POST: never on the repairable contract hull.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationConstructionYardProtectedGridComponent : Component
{
    public readonly Dictionary<Vector2i, Tile> BlueprintTiles = new();
    public bool IsRestoring;
}

/// <summary>
/// Protects the platform's mapped fabric from tile edits (including the grid
/// splitter), ordinary damage, wrenching and construction-graph dismantling.
/// Does not confer invulnerability upon people, loose cargo or contract hulls.
/// </summary>
public sealed class OrganizationConstructionYardProtectionSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<OrganizationConstructionYardProtectedGridComponent,
            TileChangedEvent>(OnYardTileChanged);
    }

    public bool Protect(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return false;

        var protectedGrid = EnsureComp<OrganizationConstructionYardProtectedGridComponent>(gridUid);
        if (protectedGrid.BlueprintTiles.Count != 0)
            return true;

        foreach (var tile in _maps.GetAllTiles(gridUid, grid))
            protectedGrid.BlueprintTiles.Add(tile.GridIndices, tile.Tile);

        if (protectedGrid.BlueprintTiles.Count == 0)
            return false;

        EnsureComp<GodmodeComponent>(gridUid);

        // Only permanent, physically anchored fixtures belong to the protected
        // station. Do not modify portable items: users can still bring supplies.
        var children = Transform(gridUid).ChildEnumerator;
        try
        {
            while (children.MoveNext(out var child))
            {
                if (TerminatingOrDeleted(child) || !Transform(child).Anchored)
                    continue;

                EnsureComp<GodmodeComponent>(child);
                RemComp<AnchorableComponent>(child);
                RemComp<ConstructionComponent>(child);
            }
        }
        finally
        {
            children.Dispose();
        }

        return true;
    }

    private void OnYardTileChanged(Entity<OrganizationConstructionYardProtectedGridComponent> ent,
        ref TileChangedEvent args)
    {
        if (ent.Comp.IsRestoring)
            return;

        var rollback = new List<(Vector2i GridIndices, Tile Tile)>();
        foreach (var change in args.Changes)
        {
            var required = ent.Comp.BlueprintTiles.TryGetValue(change.GridIndices,
                out var blueprint) ? blueprint : Tile.Empty;

            if (change.NewTile.TypeId != required.TypeId)
                rollback.Add((change.GridIndices, required));
        }

        if (rollback.Count == 0)
            return;

        ent.Comp.IsRestoring = true;
        try
        {
            _maps.SetTiles(ent.Owner, args.Entity.Comp, rollback);
        }
        finally
        {
            ent.Comp.IsRestoring = false;
        }
    }
}
