using System;
using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationInvestmentPlaytimeRulesTest
{
    [TestCase(0, false, 90)]
    [TestCase(60, false, 30)]
    [TestCase(89, false, 1)]
    [TestCase(90, true, 0)]
    [TestCase(120, true, 0)]
    public void Eligibility(int minutes, bool eligible, int remaining)
    {
        var time = TimeSpan.FromMinutes(minutes);
        Assert.That(OrganizationInvestmentPlaytimeRules.Eligible(time), Is.EqualTo(eligible));
        Assert.That(OrganizationInvestmentPlaytimeRules.RemainingMinutes(time), Is.EqualTo(remaining));
    }

    [Test]
    public void SlotsHaveDifferentTrackers()
    {
        Assert.That(OrganizationInvestmentPlaytimeRules.Tracker(0), Is.Not.EqualTo(OrganizationInvestmentPlaytimeRules.Tracker(1)));
        Assert.That(OrganizationInvestmentPlaytimeRules.Eligible(TimeSpan.FromSeconds(5399)), Is.False);
    }
}
