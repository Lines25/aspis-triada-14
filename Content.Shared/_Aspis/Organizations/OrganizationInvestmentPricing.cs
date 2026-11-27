using System;

namespace Content.Shared._Aspis.Organizations;

/// <summary>Marginal pricing in basis points; splitting a purchase cannot lower its cost.</summary>
public static class OrganizationInvestmentPricing
{
    public static (int Gained, long Spent) Calculate(
        OrganizationMarketScope scope, int owned, int free, int budgetShare, long budget)
    {
        if (owned < 0 || owned > 10_000 || free <= 0 || budget <= 0)
            return (0, 0);

        var basePrice = scope == OrganizationMarketScope.Sector ? 50_000L : 20_000L;
        var multiplier = 120 - Math.Clamp(budgetShare, 0, 100) / 5;
        var available = Math.Min(free, 10_000 - owned);
        var gained = 0;
        long spent = 0;
        while (gained < available)
        {
            var position = owned + gained;
            var price = (basePrice + position * 100L) * multiplier / 100;
            if (position >= 1800)
                price *= 2;
            if (price > budget - spent)
                break;
            spent += price;
            gained++;
        }
        return (gained, spent);
    }
}
