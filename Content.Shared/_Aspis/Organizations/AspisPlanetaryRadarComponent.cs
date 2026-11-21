using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// A round-local navigational mode marker. It is placed on the map entity,
/// never on shuttle grids, so it cannot leak into the next space map after FTL.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AspisPlanetaryRadarComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Surface;
}
