using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Round-local prototype for an installed agricultural spray unit.
/// The chemical/market integration will be added after the authored maps arrive.
/// </summary>
[RegisterComponent]
public sealed partial class AspisAgriculturalSprayerComponent : Component
{
    [DataField]
    public int Tank = 0;

    [DataField]
    public int TankCapacity = 160;

    public bool Spraying;

    public bool HerbicideLoaded;

    // A physical sprayer is single-use for one accepted planetary quota.
    public bool Spent;
}
