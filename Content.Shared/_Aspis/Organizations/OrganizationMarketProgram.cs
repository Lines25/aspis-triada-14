using System;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

[Serializable, NetSerializable]
public enum OrganizationMarketProgram : byte
{
    None,
    PriceCampaign,
    PriorityContracts
}

[Serializable, NetSerializable]
public sealed class OrganizationMarketProgramMessage(Guid organization, OrganizationMarketProgram program)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationMarketProgram Program = program;
}

[Serializable, NetSerializable]
public sealed class OrganizationInvestorPrivacyMessage(Guid organization, bool visible)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public bool Visible = visible;
}

public static class OrganizationMarketProgramRules
{
    public const int Duration = 3;

    public static bool Valid(OrganizationMarketProgram program, int cycles) =>
        Enum.IsDefined(program) && (program == OrganizationMarketProgram.None
            ? cycles == 0 : cycles is >= 1 and <= Duration);

    public static double DemandMultiplier(OrganizationMarketProgram program) => program switch
    {
        OrganizationMarketProgram.PriceCampaign => 1.35,
        OrganizationMarketProgram.PriorityContracts => 1.50,
        _ => 1.0
    };

    public static int ExtraCost(OrganizationMarketProgram program, int gross)
    {
        var rate = program switch
        {
            OrganizationMarketProgram.PriceCampaign => 12,
            OrganizationMarketProgram.PriorityContracts => 20,
            _ => 0
        };
        return (int)((Math.Max(0, gross) * (long)rate + 99) / 100);
    }

    // The game reviews observed demand and margins, not merely a selected program.
    // A result is a lead for TDF, not proof of guilt.
    public static int ReviewLevel(OrganizationMarketProgram program, int remaining,
        int gross, int costs, long industryGross, int competingCompanies)
    {
        if (program == OrganizationMarketProgram.None || remaining >= Duration ||
            remaining <= 0 || gross < 250 || industryGross < 500 || competingCompanies <= 0)
            return 0;
        var share = 100L * gross / industryGross;
        return program switch
        {
            OrganizationMarketProgram.PriceCampaign when share >= 50 && costs >= gross => 3,
            OrganizationMarketProgram.PriceCampaign when share >= 40 && 100L * costs >= 85L * gross => 2,
            OrganizationMarketProgram.PriorityContracts when share >= 70 => 3,
            OrganizationMarketProgram.PriorityContracts when share >= 50 => 2,
            _ => 0
        };
    }
}
