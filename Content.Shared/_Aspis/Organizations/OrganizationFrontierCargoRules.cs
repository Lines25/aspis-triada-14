namespace Content.Shared._Aspis.Organizations;

/// <summary>Validated in BOTH client preview and server before accepting a route.</summary>
public static class OrganizationFrontierCargoRules
{
    public const int Minimum = 20;
    public const int Maximum = 80;
    public const int Default = 40;
    public static bool Valid(int count) => count >= Minimum && count <= Maximum;
    // 20/40/80 units => 0.35% / 0.55% / 0.95%; never more than 1.10%.
    public static int InfluenceBasisPoints(int count) => Valid(count) ? System.Math.Min(110, 15 + count) : 0;
    public static int[] Destinations(int count)
    {
        var targets = new int[4];
        if (!Valid(count)) return targets;
        for (var i = 0; i < count; i++) targets[i % 4]++;
        return targets;
    }
}
