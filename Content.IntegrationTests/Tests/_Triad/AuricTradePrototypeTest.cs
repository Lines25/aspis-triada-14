using Content.Server._Aspis.Organizations;
using Content.Server._NF.Trade;
using Content.Server.Maps;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad;

[TestFixture]
public sealed class AuricTradePrototypeTest
{
    [Test]
    public async Task AuricHubAndDepotMapsKeepTheirActualIdentity()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var hub = prototypes.Index<GameMapPrototype>("TradeCenter");
            Assert.That(hub.MapPath.ToString(), Is.EqualTo("/Maps/_Triad/POI/tradecenter.yml"));
            var components = hub.Stations["TradeCenter"].StationComponentOverrides;
            Assert.That(components.TryGetComponent("OrganizationTradeHub", out var marker), Is.True);
            Assert.That(marker, Is.TypeOf<OrganizationTradeHubComponent>());
            Assert.That(components.TryGetComponent("TradeCrateDestination", out var destination), Is.True);
            Assert.That(((TradeCrateDestinationComponent) destination!).DestinationProto.Id, Is.EqualTo("Trade"));

            foreach (var (id, path) in new[]
                     {
                         ("TradeDepot", "/Maps/_Triad/POI/tradedepot.yml"),
                         ("TradeDepotAlt", "/Maps/_Triad/POI/tradedepotalt.yml")
                     })
            {
                var depot = prototypes.Index<GameMapPrototype>(id);
                Assert.That(depot.MapPath.ToString(), Is.EqualTo(path));
                var depotComponents = depot.Stations[id].StationComponentOverrides;
                Assert.That(depotComponents.TryGetComponent("OrganizationTradeHub", out _), Is.False);
                Assert.That(depotComponents.TryGetComponent("TradeCrateDestination", out var cargo), Is.True);
                Assert.That(((TradeCrateDestinationComponent) cargo!).DestinationProto.Id, Is.EqualTo("CargoA"));
            }
        });

        await pair.CleanReturnAsync();
    }
}
