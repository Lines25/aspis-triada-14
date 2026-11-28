using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationPrivateEntryRulesTest
{
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    public void ChargesOnlyForFirstActivation(bool requestedPrivate, bool purchased, bool expected)
    {
        Assert.That(OrganizationPrivateEntryRules.RequiresPurchase(requestedPrivate, purchased), Is.EqualTo(expected));
    }

    [Test]
    public void DisablingAndRestoringDoesNotRevokePurchase()
    {
        var purchased = OrganizationPrivateEntryRules.RestorePurchase(false, true);
        purchased = OrganizationPrivateEntryRules.RestorePurchase(purchased, false);
        Assert.That(purchased, Is.True);
        Assert.That(OrganizationPrivateEntryRules.RequiresPurchase(true, purchased), Is.False);
    }
}
