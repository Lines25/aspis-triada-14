using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationInvestmentPricingTest
{
    [Test]
    public void SmallInvestmentCannotBuyInfluence()
    {
        Assert.That(OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 0, 10000, 100, 10000), Is.EqualTo((0, 0L)));
        Assert.That(OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Industry, 0, 10000, 100, 10000), Is.EqualTo((0, 0L)));
    }

    [Test]
    public void LargerBudgetsAreAcceptedAndSpendOnlyForWholePoints()
    {
        var result = OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 0, 10000, 100, 1000000);
        Assert.That(result.Gained, Is.EqualTo(19));
        Assert.That(result.Spent, Is.EqualTo(967100L));
    }

    [Test]
    public void SplittingInvestmentDoesNotReduceMarginalCost()
    {
        var first = OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 50, 1000, 25, 300000);
        var second = OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 50 + first.Gained, 1000 - first.Gained, 25, 700000);
        var combined = OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 50, 1000, 25, first.Spent + second.Spent);
        Assert.That(combined, Is.EqualTo((first.Gained + second.Gained, first.Spent + second.Spent)));
    }

    [Test]
    public void RespectsFreeCapacityAndMarketLimit()
    {
        Assert.That(OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 200, 0, 100, 1000000), Is.EqualTo((0, 0L)));
        var result = OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Industry, 9999, 50, 100, 1000000000);
        Assert.That(result.Gained, Is.EqualTo(1));
        Assert.That(result.Spent, Is.EqualTo(2039800L));
    }

    [Test]
    public void ExpansionThresholdUsesMarginalPrice()
    {
        var result = OrganizationInvestmentPricing.Calculate(
            OrganizationMarketScope.Sector, 1799, 2, 100, 689900);
        Assert.That(result, Is.EqualTo((2, 689900L)));
    }
}
