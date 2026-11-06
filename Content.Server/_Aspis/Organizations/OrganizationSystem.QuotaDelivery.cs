using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Server.Cargo.Systems;
using Content.Shared.Database;
using Robust.Shared.Audio;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Cargo Pallet Sales is the sole trusted completion path for physical delivery quotas.
/// PDA/Console requests cannot call this. One real crate may advance one post once.
/// </summary>
public sealed partial class OrganizationSystem
{
    // Verified loss of a physical crate is a company operating cost, not a fine
    // against an arbitrarily selected player. Its destruction cannot pay the owner.
    private static readonly SoundSpecifier QuotaCargoLossSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");

    private void OnQuotaCratesSold(ref EntitySoldEvent args)
    {
        foreach (var sold in args.Sold)
        {
            // Export crates must be verified against real contents before the
            // normal cargo system destroys them. Paper by itself proves nothing.
            if (TryComp<OrganizationQuotaExportComponent>(sold, out var export) ||
                TryFindExportManifestInsideCrate(sold, out export))
                TryCompleteQuotaExportOnSale(sold, export);
            if (!TryComp<OrganizationQuotaCrateComponent>(sold, out var manifest) ||
                !_quotaCargoSystem.IsQuotaCrateAtDestination(sold, manifest))
                continue;

            // Each physical dispatch has a fresh opaque reference, and the quota
            // system also stores globally accepted references for this round.
            // Delivered before the normal pallet-sale deletion: no loss penalty.
            manifest.DeliveryAccepted = true;
            TryRecordVerifiedQuotaCargo(manifest.Organization, manifest.Code,
                manifest.Reference, manifest.Post, 1);
        }
    }
    /// <summary>
    /// Only actual destruction of a tagged, undelivered crate can create a loss.
    /// A late delivery, an unapproved order, or failure to print cannot charge anyone.
    /// This is a capped company insurance expense: never credited to its owner.
    /// </summary>
    private void OnQuotaCrateRemoved(EntityUid uid, OrganizationQuotaCrateComponent manifest,
        ComponentShutdown args)
    {
        if (manifest.DeliveryAccepted || string.IsNullOrWhiteSpace(manifest.Reference) ||
            !_store.Available || !_organizations.TryGetValue(manifest.Organization, out var org) ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;

        var active = _sectorQuotaClaims.FirstOrDefault(pair =>
            pair.Value.Organization == manifest.Organization && pair.Value.Code == manifest.Code);
        var claim = active.Value;
        if (claim == null || claim.Completed || _gameTiming.CurTime >= claim.Deadline ||
            claim.ProcessedCargoReferences.Contains(manifest.Reference) ||
            claim.LostCargoReferences.Contains(manifest.Reference) ||
            manifest.Post is < 0 or >= 4)
            return;

        // At most one penalty for one physical crate. No penalty if the company
        // cannot cover it; the missing shipment itself still fails the quota.
        var damageCost = 1500 + active.Key.Slot * 500;
        var charged = Math.Min(damageCost, Math.Max(0, org.Treasury));
        if (charged > 0 && !Commit(() =>
            {
                org.Treasury -= charged;
                org.OperatingCosts += charged;
            }))
            return;
        claim.LostCargoReferences.Add(manifest.Reference);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Destroyed quota crate: {manifest.Reference}; quota={manifest.Code}; companyLoss={charged}");

        if (charged <= 0)
            return;
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } actor ||
                !TryGetCharacter(actor, out var character, out _) || character != claim.Assignee)
                continue;
            _audio.PlayEntity(QuotaCargoLossSound, actor, actor);
            _quotaPopup.PopupEntity($"Втрачено вантаж {manifest.Reference}. Витрати компанії: {charged:N0} $.",
                actor, actor);
            break;
        }
    }
}
