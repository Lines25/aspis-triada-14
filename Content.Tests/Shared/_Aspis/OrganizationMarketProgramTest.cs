using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationMarketProgramTest
{
    [Test]
    public void OldSaveHasNoProgram()
    {
        Assert.That(OrganizationMarketProgramRules.Valid(OrganizationMarketProgram.None, 0), Is.True);
        Assert.That(OrganizationMarketProgramRules.Valid(OrganizationMarketProgram.None, 1), Is.False);
        Assert.That(OrganizationMarketProgramRules.ExtraCost(OrganizationMarketProgram.None, 1000), Is.Zero);
    }

    [Test]
    public void CostsAreNotFreeDemand()
    {
        Assert.That(OrganizationMarketProgramRules.ExtraCost(OrganizationMarketProgram.PriceCampaign, 1000), Is.EqualTo(120));
        Assert.That(OrganizationMarketProgramRules.ExtraCost(OrganizationMarketProgram.PriorityContracts, 1000), Is.EqualTo(200));
    }

    [Test]
    public void ReviewsRequireActualRepeatedCycleAndCompetition()
    {
        Assert.That(OrganizationMarketProgramRules.ReviewLevel(OrganizationMarketProgram.PriceCampaign, 3, 600, 700, 1000, 1), Is.Zero);
        Assert.That(OrganizationMarketProgramRules.ReviewLevel(OrganizationMarketProgram.PriceCampaign, 2, 600, 700, 1000, 0), Is.Zero);
        Assert.That(OrganizationMarketProgramRules.ReviewLevel(OrganizationMarketProgram.PriceCampaign, 2, 600, 700, 1000, 1), Is.EqualTo(3));
        Assert.That(OrganizationMarketProgramRules.ReviewLevel(OrganizationMarketProgram.PriorityContracts, 2, 400, 200, 1000, 1), Is.Zero);
        Assert.That(OrganizationMarketProgramRules.ReviewLevel(OrganizationMarketProgram.PriorityContracts, 2, 700, 400, 1000, 1), Is.EqualTo(3));
        Assert.That(OrganizationMarketProgramRules.ReviewLevel(OrganizationMarketProgram.PriorityContracts, 2, 7, 4, 10, 1), Is.Zero);
    }
}
