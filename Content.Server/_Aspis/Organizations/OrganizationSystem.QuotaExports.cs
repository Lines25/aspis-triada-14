using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Botany.Components;
using Content.Server.Research.Disk;
using Content.Server.Storage.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared._NF.Whitelist.Components;
using Content.Shared.Interaction;
using Content.Shared.Research.TechnologyDisk.Components;
using Content.Shared.Stacks;
using Content.Shared.Chemistry.Components;
using Robust.Shared.Containers;
using Content.Shared.Database;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Physical export quotas for research, agriculture, mining and manufacturing.
/// A printed invoice seals a REAL crate, then the cargo pallet sale validates
/// recursively contained goods before advancing a single unit of the quota.
/// This cannot be completed by PDA/UI input or by an empty labeled crate.
/// </summary>
public sealed partial class OrganizationSystem
{
    private static bool UsesQuotaExport(OrganizationQuotaWorkflow workflow) => workflow is
        OrganizationQuotaWorkflow.ResearchExport or
        OrganizationQuotaWorkflow.AgriculturalExport or
        OrganizationQuotaWorkflow.MiningExport or
        OrganizationQuotaWorkflow.IndustrialExport or
        OrganizationQuotaWorkflow.TradeExport or
        OrganizationQuotaWorkflow.PharmaceuticalExport;

    private void OnAttachQuotaExportInvoice(EntityUid crate, EntityStorageComponent storage,
        InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<OrganizationQuotaChipComponent>(args.Used, out var invoice))
            return;
        var prototype = MetaData(crate).EntityPrototype?.ID ?? string.Empty;
        // Do not allow sealing a backpack, a locker or a logistics crate as
        // new merchandise merely because it has a ContainerManagerComponent.
        if ((!HasComp<NFCrateComponent>(crate) &&
             !prototype.Contains("Crate", StringComparison.OrdinalIgnoreCase)) ||
            HasComp<OrganizationQuotaCrateComponent>(crate))
            return;
        args.Handled = true;
        // Failed/rejected pharmaceutical shipments can be corrected and
        // re-sealed with a freshly issued invoice. A verified sale is final.
        // The previous rejection remains in the quality-audit history.
        if ((TryComp<OrganizationQuotaExportComponent>(crate, out var existing) &&
             existing.Verified) ||
            !TryGetCharacter(args.User, out var character, out _) ||
            !IsOriginalController(args.User) || !_store.Available ||
            FindMembership(character) is not { } org || org.Id != invoice.Organization ||
            !UsesQuotaExport(OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry)))
        {
            _quotaPopup.PopupEntity("Неможливо оформити цей ящик для квоти.", args.User, args.User);
            return;
        }

        ExpireSectorQuotas();
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == org.Id && c.Code == invoice.Code &&
            c.Assignee == character && !c.Completed);
        if (claim == null || _gameTiming.CurTime >= claim.Deadline ||
            claim.Delivered >= claim.Units)
        {
            _quotaPopup.PopupEntity("Квота неактивна або термін виконання закінчився.", args.User, args.User);
            return;
        }

        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        // Pharmaceutical shipments must be sealed with the invoice applied to
        // the crate itself. Paper found inside a box proves authorship of that
        // paper but not who changed the drugs after packaging.
        var pharmaceuticalSeal = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport
            ? PharmaceuticalContentsSeal(crate) : string.Empty;
        if (workflow == OrganizationQuotaWorkflow.PharmaceuticalExport &&
            string.IsNullOrEmpty(pharmaceuticalSeal))
        {
            _quotaPopup.PopupEntity("Спочатку покладіть продукцію в ящик, потім прикріпіть накладну.",
                args.User, args.User);
            return;
        }
        var seal = EnsureComp<OrganizationQuotaExportComponent>(crate);
        seal.Organization = org.Id;
        seal.Code = claim.Code;
        seal.Reference = $"EXPORT-{Guid.NewGuid():N}";
        seal.PharmaceuticalSeal = pharmaceuticalSeal;
        var destination = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport
            ? Array.FindIndex(claim.PostTargets, count => count > 0) : -1;
        var destinationText = destination >= 0 ? $" | Пост {(char)('A' + destination)}" : string.Empty;
        _organizationMetadata.SetEntityName(crate,
            $"Експорт {claim.Code}{destinationText} | {seal.Reference[^8..]}");
        _quotaPopup.PopupEntity($"Накладну {claim.Code} закріплено. Склад зафіксовано.{destinationText}",
            args.User, args.User);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Quota export invoice sealed: {claim.Code} crate={crate} ref={seal.Reference}");
        QueueDel(args.Used);
    }

    // Some crate interaction handlers insert paper before other InteractUsing
    // subscribers see the event. A physical invoice INSIDE the sold crate is an
    // equally valid authenticated waybill: it was spawned by the server, cannot
    // be forged by copying the printed text, and is consumed in the same sale.
    private bool TryFindExportManifestInsideCrate(EntityUid crate,
        out OrganizationQuotaExportComponent seal)
    {
        seal = default!;
        if (!HasComp<EntityStorageComponent>(crate))
            return false;
        var prototype = MetaData(crate).EntityPrototype?.ID ?? string.Empty;
        if (!HasComp<NFCrateComponent>(crate) &&
            !prototype.Contains("Crate", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryComp<ContainerManagerComponent>(crate, out var root))
            return false;
        var toCheck = new Stack<(EntityUid Entity, int Depth)>();
        var visited = new HashSet<EntityUid>();
        foreach (var container in root.Containers.Values)
            foreach (var item in container.ContainedEntities)
                toCheck.Push((item, 1));
        while (toCheck.Count > 0 && visited.Count < 512)
        {
            var (entity, depth) = toCheck.Pop();
            if (!visited.Add(entity) || depth > 12)
                continue;
            if (TryComp<OrganizationQuotaChipComponent>(entity, out var waybill) &&
                _sectorQuotaClaims.Values.Any(c => c.Organization == waybill.Organization &&
                    c.Code == waybill.Code && !c.Completed && _gameTiming.CurTime < c.Deadline))
            {
                seal = EnsureComp<OrganizationQuotaExportComponent>(crate);
                seal.Organization = waybill.Organization;
                seal.Code = waybill.Code;
                seal.Reference = $"EXPORT-{Guid.NewGuid():N}";
                return true;
            }
            if (!TryComp<ContainerManagerComponent>(entity, out var nested))
                continue;
            foreach (var container in nested.Containers.Values)
                foreach (var contained in container.ContainedEntities)
                    toCheck.Push((contained, depth + 1));
        }
        return false;
    }

    // Called by Cargo BEFORE sale prices and deletions are calculated. Physical
    // quota exports are zero-price shipments: the company is paid only by the
    // quota ledger after verification. Invalid or wrong-post crates stay on pallet.
    public bool TryPrepareQuotaExportForSale(EntityUid crate, out bool isQuotaExport)
    {
        isQuotaExport = TryComp<OrganizationQuotaExportComponent>(crate, out var seal) ||
            TryFindExportManifestInsideCrate(crate, out seal);
        if (!isQuotaExport || seal == null || seal.Verified ||
            !_organizations.TryGetValue(seal.Organization, out var org) ||
            org.Kind != OrganizationKind.Company || !_store.Available)
            return false;

        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        if (!UsesQuotaExport(workflow))
            return false;
        var job = _sectorQuotaClaims.FirstOrDefault(p =>
            p.Value.Organization == seal.Organization && p.Value.Code == seal.Code);
        if (job.Value == null || job.Value.Completed ||
            _gameTiming.CurTime >= job.Value.Deadline ||
            job.Value.Delivered >= job.Value.Units)
            return false;
        if (workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
        {
            var post = Array.FindIndex(job.Value.PostTargets, count => count > 0);
            if (post < 0 || !_quotaCargoSystem.IsQuotaExportAtDestination(crate, post))
                return false;
            if (!HasUntamperedPharmaceuticalSeal(crate, seal))
            {
                RecordRejectedPharmaceuticalShipment(job.Value, seal,
                    new PharmaceuticalInspection(0, true,
                        "Накладну не прикріплено або вміст змінено після пломбування"));
                return false;
            }
            var inspection = InspectPharmaceuticalCrate(crate, job.Key.Slot,
                job.Value.PharmaceuticalVariant);
            var minimumQuality = job.Key.Slot == 3 ? 75 : 50;
            if (inspection.WrongMedicine || inspection.QualityPercent < minimumQuality)
            {
                RecordRejectedPharmaceuticalShipment(job.Value, seal, inspection);
                return false;
            }
            return true;
        }

        return CountVerifiedExportContents(crate, workflow) >=
            OrganizationQuotaRules.ExportItemsPerCrate(workflow, job.Key.Slot);
    }

    private void TryCompleteQuotaExportOnSale(EntityUid crate, OrganizationQuotaExportComponent seal)
    {
        if (seal.Verified || !_organizations.TryGetValue(seal.Organization, out var org) ||
            org.Kind != OrganizationKind.Company)
            return;
        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        if (!UsesQuotaExport(workflow))
            return;
        var job = _sectorQuotaClaims.FirstOrDefault(p =>
            p.Value.Organization == seal.Organization && p.Value.Code == seal.Code);
        if (job.Value == null || job.Value.Completed || _gameTiming.CurTime >= job.Value.Deadline)
            return;

        if (workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
        {
            var post = Array.FindIndex(job.Value.PostTargets, count => count > 0);
            if (post < 0 || !_quotaCargoSystem.IsQuotaExportAtDestination(crate, post))
                return;
            if (!HasUntamperedPharmaceuticalSeal(crate, seal))
            {
                RecordRejectedPharmaceuticalShipment(job.Value, seal,
                    new PharmaceuticalInspection(0, true,
                        "Накладну не прикріплено або вміст змінено після пломбування"));
                return;
            }
            var pharmacy = InspectPharmaceuticalCrate(crate, job.Key.Slot,
                job.Value.PharmaceuticalVariant);
            if (pharmacy.WrongMedicine)
            {
                // Never invoice a player for another person's sabotage. Reject
                // unsafe merchandise and log the proven contents for audit.
                RecordRejectedPharmaceuticalShipment(job.Value, seal, pharmacy);
                return;
            }
            var minimumQuality = job.Key.Slot == 3 ? 75 : 50;
            if (pharmacy.QualityPercent < minimumQuality ||
                !TryRecordVerifiedQuotaCargo(org.Id, seal.Code, seal.Reference, post, 1,
                    pharmacy.QualityPercent))
                return;
            job.Value.LastInspectionReport =
                $"{pharmacy.Report}; знижка за якість {100 - pharmacy.QualityPercent}%";
            job.Value.LastInspectionQuality = Math.Clamp(
                job.Value.VerifiedQualityTotal / Math.Max(1, job.Value.Delivered), 0, 100);
            seal.Verified = true;
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"Pharmaceutical crate verified: {seal.Reference}, quality={pharmacy.QualityPercent}%");
            if (pharmacy.QualityPercent < 100)
                NotifyQuotaAssignee(job.Value,
                    $"Партію прийнято зі знижкою: комплектність {pharmacy.QualityPercent}%.", false);
            return;
        }

        var required = OrganizationQuotaRules.ExportItemsPerCrate(workflow, job.Key.Slot);
        var actual = CountVerifiedExportContents(crate, workflow);
        if (actual < required)
        {
            _adminLog.Add(LogType.Action, LogImpact.Low,
                $"Rejected quota export {seal.Reference}: cargo {actual}/{required} for {workflow}");
            return;
        }

        // The crate's physical ID, not text from PDA or player, is the proof.
        if (!TryRecordVerifiedQuotaCargo(org.Id, seal.Code, seal.Reference, 0, 1))
            return;
        seal.Verified = true;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Verified export crate {seal.Reference}: category={workflow}, items={actual}");
    }

    private bool HasUntamperedPharmaceuticalSeal(EntityUid crate,
        OrganizationQuotaExportComponent seal) =>
        !string.IsNullOrEmpty(seal.PharmaceuticalSeal) &&
        string.Equals(seal.PharmaceuticalSeal, PharmaceuticalContentsSeal(crate),
            StringComparison.Ordinal);

    // Inspections may run several times as a cargo pallet updates. Record each
    // server-issued manifest reference only once so a player cannot inflate the
    // defect count or repeatedly play error notifications by reselling a crate.
    private void RecordRejectedPharmaceuticalShipment(SectorQuotaClaim claim,
        OrganizationQuotaExportComponent seal, PharmaceuticalInspection inspection)
    {
        if (!claim.RejectedPharmaceuticalReferences.Add(seal.Reference))
            return;
        claim.RejectedShipments++;
        claim.LastInspectionReport = $"Відхилено: {inspection.Report}";
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Pharmaceutical export inspection rejected: ref={seal.Reference} quota={seal.Code} reason={inspection.Report}");
        NotifyQuotaAssignee(claim,
            $"Квота {seal.Code}: партію відхилено. {inspection.Report}", false);
    }

    private int CountVerifiedExportContents(EntityUid crate, OrganizationQuotaWorkflow workflow)
    {
        var visited = new HashSet<EntityUid>();
        var nodes = new Stack<(EntityUid Entity, int Depth)>();
        if (!TryComp<ContainerManagerComponent>(crate, out var root))
            return 0;
        foreach (var container in root.Containers.Values)
            foreach (var item in container.ContainedEntities)
                nodes.Push((item, 1));

        var count = 0;
        // Hard limit on depth and entity visits. Cycles/malformed containers cannot
        // hang cargo sales or be used to duplicate the same inventory entity.
        while (nodes.Count > 0)
        {
            if (visited.Count >= 512)
                return 0;
            var (item, depth) = nodes.Pop();
            if (!visited.Add(item) || depth > 12)
                continue;
            count += CountExportItem(item, workflow);
            if (count > 10000)
                return 0;
            if (!TryComp<ContainerManagerComponent>(item, out var nested))
                continue;
            foreach (var container in nested.Containers.Values)
                foreach (var contained in container.ContainedEntities)
                    nodes.Push((contained, depth + 1));
        }
        return count;
    }

    private int CountExportItem(EntityUid item, OrganizationQuotaWorkflow workflow)
    {
        if (workflow == OrganizationQuotaWorkflow.ResearchExport)
        {
            if (TryComp<ResearchDiskComponent>(item, out var research) &&
                research.Points > 0 && !research.UnlockAllTech)
                return 1;
            return TryComp<TechnologyDiskComponent>(item, out var technology) &&
                technology.Recipes is { Count: > 0 } ? 1 : 0;
        }
        if (workflow == OrganizationQuotaWorkflow.AgriculturalExport)
            return HasComp<ProduceComponent>(item) || HasComp<SeedComponent>(item) ? 1 : 0;
        if (workflow == OrganizationQuotaWorkflow.TradeExport &&
            (HasComp<ProduceComponent>(item) || HasComp<SeedComponent>(item)))
            return 1;

        if (!TryComp<StackComponent>(item, out var stack) || stack.Unlimited || stack.Count <= 0)
            return 0;
        var stackType = stack.StackTypeId.Id;
        if (workflow == OrganizationQuotaWorkflow.MiningExport)
        {
            // Count mined ores only, not manufactured metals or arbitrary stacks.
            return stackType.Contains("Ore", StringComparison.OrdinalIgnoreCase) ||
                   stackType.StartsWith("Raw", StringComparison.OrdinalIgnoreCase)
                ? stack.Count : 0;
        }
        if (workflow == OrganizationQuotaWorkflow.TradeExport &&
            stackType is "Cloth" or "Leather" or "Wood" or "Glass" or "Plastic" or "Steel" or "Cardboard")
            return stack.Count;
        // Industrial deliveries require processed building materials.
        if (workflow == OrganizationQuotaWorkflow.IndustrialExport &&
            stackType is "Steel" or "Glass" or "Plasteel" or "Plastic" or "Wood" or "MetalRod" or "ReinforcedGlass")
            return stack.Count;
        return 0;
    }
}
