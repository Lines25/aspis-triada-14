using System;

namespace Content.Shared._Aspis.Organizations;

public static class OrganizationInvestmentPlaytimeRules
{
    public static readonly TimeSpan Required = TimeSpan.FromMinutes(90);
    public static string Tracker(int slot) => $"AspisInvestmentCharacter:{slot}";
    public static int RemainingMinutes(TimeSpan played) =>
        (int)Math.Ceiling(Math.Max(0, (Required - played).TotalMinutes));
    public static bool Eligible(TimeSpan played) => played >= Required;
}
