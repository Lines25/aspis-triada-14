using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Content.Server.Shuttles.Components;

namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// Shipyard-owned deed operations for _Aspis organization rentals. Kept in a
/// ShipyardSystem partial to obey ShuttleDeedComponent's Access restrictions.
/// No personal ship ownership or resale rights are conferred by a rental.
/// </summary>
public sealed partial class ShipyardSystem
{
    public bool TryRegisterCorporateRentalDeed(EntityUid card, EntityUid grid,
        string shuttleName, string renterName)
    {
        if (TerminatingOrDeleted(card) || TerminatingOrDeleted(grid) ||
            !HasComp<Content.Shared.Access.Components.IdCardComponent>(card) ||
            HasComp<ShuttleDeedComponent>(card))
            return false;

        var listedName = shuttleName.Length > ShuttleDeedComponent.MaxNameLength
            ? shuttleName[..ShuttleDeedComponent.MaxNameLength] : shuttleName;
        var cardDeed = EnsureComp<ShuttleDeedComponent>(card);
        AssignShuttleDeedProperties(cardDeed, grid, listedName, renterName,
            purchasedWithVoucher: false, purchaseVoucherUid: null, loadedFromSave: true);
        cardDeed.DeedHolder = card;
        Dirty(card, cardDeed);

        var gridDeed = EnsureComp<ShuttleDeedComponent>(grid);
        AssignShuttleDeedProperties(gridDeed, grid, listedName, renterName,
            purchasedWithVoucher: false, purchaseVoucherUid: null, loadedFromSave: true);
        gridDeed.DeedHolder = card;
        Dirty(grid, gridDeed);

        // Mirror Shipyard's load path: every helm must point to this grid's
        // deed, not to an old ShuttleId embedded in the signed saved manifest.
        var helms = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (helms.MoveNext(out var helm, out _, out var xform))
        {
            if (xform.GridUid != grid)
                continue;
            var lockComp = EnsureComp<ShuttleConsoleLockComponent>(helm);
            _shuttleConsoleLock.SetShuttleId(helm, grid.ToString(), lockComp);
        }
        return true;
    }

    public void RemoveCorporateRentalDeed(EntityUid card, EntityUid grid)
    {
        if (TerminatingOrDeleted(card))
            return;
        if (TryComp<ShuttleDeedComponent>(card, out var deed) &&
            deed.ShuttleUid == grid && deed.LoadedFromSave)
            RemComp<ShuttleDeedComponent>(card);
    }
}
