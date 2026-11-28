using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationTradeDestinationRulesTest
{
    [TestCase(0, "CargoA")]
    [TestCase(1, "CargoB")]
    [TestCase(2, "CargoC")]
    [TestCase(3, "CargoD")]
    public void QuotaPostsUseRealDepotIds(int post, string expected)
    {
        Assert.That(OrganizationTradeDestinationRules.TryGetDepotId(post, out var id), Is.True);
        Assert.That(id, Is.EqualTo(expected));
    }

    [TestCase(-1)]
    [TestCase(4)]
    public void InvalidPostsCannotResolveToTradeHubOrOtherDepots(int post)
    {
        Assert.That(OrganizationTradeDestinationRules.TryGetDepotId(post, out var id), Is.False);
        Assert.That(id, Is.Empty);
    }
}
