using System.Linq;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Components;
using Robust.Shared.Prototypes;

// Aspis extension point: the existing Cargo Requests queue, not a second
// cargo implementation. Only this companion method belongs to our module.
namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
        /// <summary>
        /// Reuses ordinary Trade Mall Cargo Requests for a single faction field
        /// kit crate. The Aspis organization system authenticates and consumes
        /// its one-use physical invoice before calling this method.
        /// </summary>
        public bool TryQueueOrganizationFactionKit(EntityUid console, Guid organization,
            string code, string requester)
        {
            const string product = "AspisFactionFieldSupplyCrate";
            if (organization == Guid.Empty || code.Length is < 6 or > 48 ||
                requester.Length is < 1 or > 100 ||
                !TryComp<CargoOrderConsoleComponent>(console, out var control) ||
                !IsQuotaTradeMall(Transform(console).GridUid) ||
                !_protoMan.HasIndex<EntityPrototype>(product) ||
                !TryGetOrderDatabase(console, out var station, out var database, control) ||
                database.Capacity - GetOutstandingOrderCount(database) -
                    database.Orders.Where(order => !order.Approved).Sum(order => order.OrderQuantity) < 1)
                return false;
            // A physical Cargo order, not instantaneous equipment printing.
            var order = new CargoOrderData(GenerateOrderId(database), product,
                "Експедиційне обладнання", 0, 1, requester,
                $"[ASPIS-FIELD:{code}] Комплект фракції. Схвалити та доставити.",
                EntityManager.GetNetEntity(console));
            return TryAddOrder(station!.Value, order, database);
        }

}
