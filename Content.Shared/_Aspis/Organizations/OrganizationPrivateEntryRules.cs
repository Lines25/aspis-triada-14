namespace Content.Shared._Aspis.Organizations;

/// <summary>One-time entitlement, separate from the current admission mode.</summary>
public static class OrganizationPrivateEntryRules
{
    public const int Price = 75_000;

    public static bool RequiresPurchase(bool requestedPrivate, bool purchased) =>
        requestedPrivate && !purchased;

    // Older private organizations paid at founding even if the flag was absent.
    // Disabling private entry never removes an existing entitlement.
    public static bool RestorePurchase(bool purchased, bool isPrivate) =>
        purchased || isPrivate;
}
