using System;

namespace Content.Shared._Aspis.Organizations;

public static class OrganizationCargoCatalogRules
{
    public static bool IsAvailable(string orderId, string entityId, string displayName)
    {
        // Preserve the individual planetary parachute even on translated catalogues.
        if (orderId == "AspisPlanetaryParachuteOrder" || entityId == "AspisPlanetaryParachuteBackpack")
            return true;

        // Older local builds used a separate multi-parachute supply crate.
        // Do not remove unrelated emergency supplies or an individual parachute.
        var ids = orderId + " " + entityId;
        var parachute = ids.Contains("parachut", StringComparison.OrdinalIgnoreCase);
        var bundle = ids.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
            ids.Contains("kit", StringComparison.OrdinalIgnoreCase) ||
            ids.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
            ids.Contains("pack", StringComparison.OrdinalIgnoreCase);
        if (parachute && bundle && !ids.Contains("backpack", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = displayName.Trim();
        return !(name.Contains("парашут", StringComparison.OrdinalIgnoreCase) &&
                    (name.Contains("комплект", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("набір", StringComparison.OrdinalIgnoreCase)) ||
                 name.Contains("parachut", StringComparison.OrdinalIgnoreCase) &&
                    (name.Contains("kit", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("bundle", StringComparison.OrdinalIgnoreCase)));
    }
}
