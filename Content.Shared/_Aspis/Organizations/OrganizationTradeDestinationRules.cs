namespace Content.Shared._Aspis.Organizations;

/// <summary>Stable trade destination IDs; station display names and spawn order are irrelevant.</summary>
public static class OrganizationTradeDestinationRules
{
    public static bool TryGetDepotId(int post, out string id)
    {
        id = post switch
        {
            0 => "CargoA",
            1 => "CargoB",
            2 => "CargoC",
            3 => "CargoD",
            _ => string.Empty
        };
        return id.Length != 0;
    }
}
