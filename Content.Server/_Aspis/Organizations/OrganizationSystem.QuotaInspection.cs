using System;
using System.Linq;
using Content.Server.Storage.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Robust.Shared.Prototypes;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Q2: physical pre-dispatch inspections, bound to a real quota, scanner and
/// cargo entity. Certified contracts still complete ONLY through Cargo sales.
/// This does not simulate a remote trip, medicine storage or medical treatment.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const string QuotaInspectionScannerProto = "AspisQuotaInspectionScanner";

    private static bool QuotaRequiresInspection(QuotaScenarioDefinition scenario) =>
        scenario.Workflow != OrganizationQuotaWorkflow.CargoDelivery &&
        scenario.Id.EndsWith(".inspected", StringComparison.Ordinal);

    private void IssueQuotaInspectionScanner(EntityUid actor, SectorQuotaClaim claim)
    {
        if (!QuotaRequiresInspection(claim.Scenario) ||
            !_prototypes.HasIndex<EntityPrototype>(QuotaInspectionScannerProto) ||
            _gameTiming.CurTime >= claim.Deadline || claim.Completed ||
            claim.InspectionScanner is { } scanner && !Deleted(scanner))
            return;

        var item = Spawn(QuotaInspectionScannerProto, Transform(actor).Coordinates);
        var marker = EnsureComp<OrganizationQuotaInspectionScannerComponent>(item);
        marker.Organization = claim.Organization;
        marker.Code = claim.Code;
        claim.InspectionScanner = item;
        _organizationMetadata.SetEntityName(item, $"Сканер перевірки {claim.Code}");
        _hands.TryPickupAnyHand(actor, item);
        _quotaPopup.PopupEntity("Видано сканер перевірки партій. Застосуйте його на кожному ящику перед здачею.",
            actor, actor);
    }

    private void CleanupQuotaInspectionScanner(SectorQuotaClaim claim)
    {
        if (claim.InspectionScanner is { } item && !Deleted(item))
            QueueDel(item);
        claim.InspectionScanner = null;
    }

    private void OnQuotaInspectionScannerUsed(EntityUid scanner, OrganizationQuotaInspectionScannerComponent marker,
        AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } crate)
            return;
        // The ordinary scanner must remain usable as an item. Only interactions
        // with storage crates are captured by this quota system.
        if (!HasComp<EntityStorageComponent>(crate) &&
            !HasComp<OrganizationQuotaCrateComponent>(crate))
            return;
        args.Handled = true;

        if (!_store.Available || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var person, out _) ||
            FindMembership(person) is not { Kind: OrganizationKind.Company } org ||
            org.Id != marker.Organization || org.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            _quotaPopup.PopupEntity("Цей сканер належить іншій компанії або користувач не авторизований.",
                args.User, args.User);
            return;
        }

        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c => c.Organization == marker.Organization &&
            c.Code == marker.Code && !c.Completed && _gameTiming.CurTime < c.Deadline);
        if (claim == null || !QuotaRequiresInspection(claim.Scenario) || claim.InspectionScanner != scanner)
        {
            _quotaPopup.PopupEntity("Сканер не прив'язаний до чинного контракту.", args.User, args.User);
            return;
        }

        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        string reference;
        string fingerprint;
        if (workflow == OrganizationQuotaWorkflow.CargoDelivery &&
            TryComp<OrganizationQuotaCrateComponent>(crate, out var delivery))
        {
            if (delivery.Organization != marker.Organization || delivery.Code != claim.Code ||
                delivery.DeliveryAccepted || string.IsNullOrWhiteSpace(delivery.Reference))
            {
                _quotaPopup.PopupEntity("Це не вантажний ящик вашого контракту.", args.User, args.User);
                return;
            }
            reference = delivery.Reference;
            fingerprint = string.Empty;
        }
        else if (workflow != OrganizationQuotaWorkflow.CargoDelivery &&
                 TryComp<OrganizationQuotaExportComponent>(crate, out var export) &&
                 UsesQuotaExport(workflow))
        {
            if (export.Verified || export.Organization != marker.Organization ||
                export.Code != claim.Code || string.IsNullOrWhiteSpace(export.Reference))
            {
                _quotaPopup.PopupEntity("Ящик має накладну іншої компанії або вже зданий.",
                    args.User, args.User);
                return;
            }
            if (workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
            {
                if (!HasUntamperedPharmaceuticalSeal(crate, export))
                {
                    _quotaPopup.PopupEntity("Після пломбування змінено вміст. Переоформіть накладну.",
                        args.User, args.User);
                    return;
                }
                var quality = InspectPharmaceuticalCrate(crate, claim.DifficultySlot,
                    claim.PharmaceuticalVariant);
                var required = claim.DifficultySlot == 3 ? 75 : 50;
                if (quality.WrongMedicine || quality.QualityPercent < required)
                {
                    _quotaPopup.PopupEntity($"Партія не пройшла перевірку: {quality.Report}",
                        args.User, args.User);
                    return;
                }
            }
            else
            {
                var required = OrganizationQuotaRules.ExportItemsPerCrate(workflow,
                    claim.DifficultySlot);
                var actual = CountVerifiedExportContents(crate, workflow);
                if (actual < required)
                {
                    _quotaPopup.PopupEntity($"Недостатній вміст для інспекції: {actual}/{required}.",
                        args.User, args.User);
                    return;
                }
            }
            reference = export.Reference;
            fingerprint = PharmaceuticalContentsSeal(crate);
            if (string.IsNullOrEmpty(fingerprint))
            {
                _quotaPopup.PopupEntity("Неможливо підтвердити фізичний вміст партії.",
                    args.User, args.User);
                return;
            }
        }
        else
        {
            _quotaPopup.PopupEntity("Спочатку оформіть контрактний ящик і прикріпіть накладну.",
                args.User, args.User);
            return;
        }

        var stamp = EnsureComp<OrganizationQuotaInspectionStampComponent>(crate);
        stamp.Organization = marker.Organization;
        stamp.Code = marker.Code;
        stamp.Reference = reference;
        stamp.ContentsFingerprint = fingerprint;
        _quotaPopup.PopupEntity($"Інспекцію вантажу {reference[^8..]} підтверджено.",
            args.User, args.User);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Q2 quota inspection: org={marker.Organization} code={claim.Code} crate={crate} ref={reference}");
    }

    private bool HasValidQuotaInspection(EntityUid crate, Guid org, string code, string reference,
        bool exportShipment)
    {
        if (!TryComp<OrganizationQuotaInspectionStampComponent>(crate, out var stamp) ||
            stamp.Organization != org || stamp.Code != code || stamp.Reference != reference)
            return false;
        if (!exportShipment)
            return string.IsNullOrEmpty(stamp.ContentsFingerprint);
        return !string.IsNullOrEmpty(stamp.ContentsFingerprint) &&
               string.Equals(stamp.ContentsFingerprint, PharmaceuticalContentsSeal(crate),
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// Called by the Cargo sell-pallet preflight BEFORE the crate can be
    /// consumed. Ordinary, non-inspected contracts preserve their old path.
    /// </summary>
    public bool CanSellQuotaDeliveryCrate(EntityUid crate, OrganizationQuotaCrateComponent manifest)
    {
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c => c.Organization == manifest.Organization &&
            c.Code == manifest.Code && !c.Completed && _gameTiming.CurTime < c.Deadline);
        if (claim == null)
            return true; // Maintain legacy disposal behaviour for expired cargo.
        // Long-haul crates are received only on the physical remote station.
        // Ordinary A-D cargo-pallet sales must never prove a frontier route.
        if (claim.Scenario.Id == FrontierScenarioId)
            return false;
        if (!QuotaRequiresInspection(claim.Scenario))
            return true;
        return HasValidQuotaInspection(crate, manifest.Organization, manifest.Code,
            manifest.Reference, false);
    }

    private bool QuotaExportInspectionApproved(EntityUid crate, SectorQuotaClaim claim,
        OrganizationQuotaExportComponent seal)
    {
        return !QuotaRequiresInspection(claim.Scenario) ||
               HasValidQuotaInspection(crate, seal.Organization, seal.Code, seal.Reference, true);
    }
}
