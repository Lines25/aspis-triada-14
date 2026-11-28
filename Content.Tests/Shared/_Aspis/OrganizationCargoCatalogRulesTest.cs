using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationCargoCatalogRulesTest
{
    [TestCase("AspisPlanetaryParachuteOrder", "AspisPlanetaryParachuteBackpack", "планетарний парашут", true)]
    [TestCase("OldOrder", "OldProduct", "комплект аварійних парашутів", false)]
    [TestCase("EmergencyParachuteKit", "CrateEmergencyParachutes", "Emergency parachute kit", false)]
    [TestCase("EmergencyToolKit", "CrateTools", "Emergency tool kit", true)]
    public void FilterOnlyParachuteBundles(string order, string product, string name, bool allowed)
    {
        Assert.That(OrganizationCargoCatalogRules.IsAvailable(order, product, name), Is.EqualTo(allowed));
    }
}
