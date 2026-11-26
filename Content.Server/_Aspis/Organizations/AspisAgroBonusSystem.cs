using Robust.Shared.IoC;

namespace Content.Server._Aspis.Organizations;

/// <summary>Round-local deferred quota bonus, paid once after normal economic settlement.</summary>
public sealed class AspisAgroBonusSystem : EntitySystem
{
    [Dependency] private OrganizationSystem _organizations = default!;
    private float _elapsed;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 2f) return;
        _elapsed = 0f;
        _organizations.ProcessAspisAgroDeferredBonuses();
    }
}
