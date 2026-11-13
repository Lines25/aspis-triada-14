using Content.Shared.Medical.Healing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Medical.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Stacks;
using Robust.Shared.Containers;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Server-only inspection of finished pharmacy products. Reagent IDs, quality
/// and case composition are taken from game entities, never printed names.
/// One stable formulation is shared by every company competing for an offer.
/// </summary>
public sealed partial class OrganizationSystem
{
    [Dependency] private SharedSolutionContainerSystem _pharmaceuticalSolutions = default!;

    private readonly record struct PharmaceuticalInspection(int QualityPercent, bool WrongMedicine,
        string Report);

    // Only items in this physical sealed container and nested inventories count.
    // A hostile/deep container tree fails closed rather than being truncated.
    private List<EntityUid> PharmaceuticalContents(EntityUid container)
    {
        var items = new List<EntityUid>();
        if (!TryComp<ContainerManagerComponent>(container, out var root))
            return items;

        var pending = new Stack<(EntityUid Item, int Depth)>();
        var visited = new HashSet<EntityUid>();
        foreach (var inventory in root.Containers.Values)
            foreach (var item in inventory.ContainedEntities)
                pending.Push((item, 1));

        while (pending.Count > 0)
        {
            if (visited.Count >= 512)
                return new List<EntityUid>();
            var (item, depth) = pending.Pop();
            if (depth > 12)
                return new List<EntityUid>();
            if (!visited.Add(item))
                continue;
            items.Add(item);
            if (!TryComp<ContainerManagerComponent>(item, out var nested))
                continue;
            foreach (var inventory in nested.Containers.Values)
                foreach (var child in inventory.ContainedEntities)
                    pending.Push((child, depth + 1));
        }
        return items;
    }

    /// <summary>
    /// Capture the actual physical contents at sealing time. Unlike the on-sale
    /// quality score, this fingerprint records item identity, parent/container,
    /// stacks and complete chemical mixtures, so replacing a correct syringe,
    /// moving supplies between kits or refilling a pill cannot go unnoticed.
    /// It is server-only and never exposed to the PDA or accepted from clients.
    /// </summary>
    private string PharmaceuticalContentsSeal(EntityUid crate)
    {
        var contents = PharmaceuticalContents(crate);
        if (contents.Count == 0)
            return string.Empty;

        var entries = new List<string>(contents.Count);
        foreach (var item in contents)
        {
            var line = new StringBuilder();
            line.Append(item).Append('|')
                .Append(Transform(item).ParentUid).Append('|')
                .Append(PharmaceuticalProto(item));

            if (TryComp<StackComponent>(item, out var stack))
                line.Append("|stack=").Append(stack.Count).Append(':').Append(stack.Unlimited);

            if (TryComp<InjectorComponent>(item, out var injector))
                AppendPharmaceuticalSolution(line, item, injector.SolutionName);
            if (HasComp<PillComponent>(item))
                AppendPharmaceuticalSolution(line, item, SharedChemMaster.PillSolutionName);

            entries.Add(line.ToString());
        }
        entries.Sort(StringComparer.Ordinal);
        var serialized = string.Join("\n", entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }

    private void AppendPharmaceuticalSolution(StringBuilder line, EntityUid item, string solutionId)
    {
        line.Append("|solution=").Append(solutionId);
        if (!_pharmaceuticalSolutions.TryGetSolution(item, solutionId, out _, out var solution) ||
            solution == null)
        {
            line.Append(":missing");
            return;
        }
        line.Append('|').Append(solution.Volume.Float().ToString("R", CultureInfo.InvariantCulture));
        foreach (var reagent in solution.Contents.OrderBy(r => r.Reagent.Prototype.Id, StringComparer.Ordinal))
        {
            line.Append('|').Append(reagent.Reagent.Prototype.Id).Append('=')
                .Append(reagent.Quantity.Float().ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private string PharmaceuticalProto(EntityUid uid) =>
        MetaData(uid).EntityPrototype?.ID ?? string.Empty;

    private bool IsPharmaceuticalKit(EntityUid uid)
    {
        var id = PharmaceuticalProto(uid);
        return (id.Contains("Medkit", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("MedicalKit", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("FirstAid", StringComparison.OrdinalIgnoreCase)) &&
               HasComp<ContainerManagerComponent>(uid);
    }

    private bool IsBandage(EntityUid uid)
    {
        if (!HasComp<HealingComponent>(uid))
            return false;
        var id = PharmaceuticalProto(uid);
        return id.Contains("Gauze", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("Bandage", StringComparison.OrdinalIgnoreCase);
    }

    private int PharmaceuticalBandages(EntityUid kit)
    {
        var count = 0;
        foreach (var item in PharmaceuticalContents(kit))
        {
            if (!IsBandage(item))
                continue;
            count += TryComp<StackComponent>(item, out var stack) && !stack.Unlimited
                ? Math.Max(0, stack.Count) : 1;
        }
        return Math.Min(4, count);
    }

    // Wrong reagent or insufficient purity is defective medicine, not an
    // underfilled but otherwise valid product. Underfilled items reduce grade.
    private (bool Correct, bool Wrong) InspectMedicine(EntityUid item, string solutionName,
        string requiredReagent, float minPurity)
    {
        if (!_pharmaceuticalSolutions.TryGetSolution(item, solutionName, out _, out var solution) ||
            solution == null || solution.Volume.Float() <= 0)
            return (false, false);

        var total = solution.Volume.Float();
        var matching = 0f;
        foreach (var reagent in solution.Contents)
        {
            if (reagent.Reagent.Prototype.Id.Equals(requiredReagent, StringComparison.OrdinalIgnoreCase))
                matching += reagent.Quantity.Float();
        }
        // 5u of the requested medicine, with >= 90% or 95% purity.
        var correct = matching >= 5f && matching / total >= minPurity;
        var wrong = !correct && total >= 5f;
        return (correct, wrong);
    }

    private (int Valid, bool Wrong) CountPharmaceuticalSyringes(IEnumerable<EntityUid> contents,
        string requiredReagent, float minPurity)
    {
        var valid = 0;
        var wrong = false;
        foreach (var item in contents)
        {
            if (!PharmaceuticalProto(item).Contains("Syringe", StringComparison.OrdinalIgnoreCase) ||
                !TryComp<InjectorComponent>(item, out var injector))
                continue;
            var result = InspectMedicine(item, injector.SolutionName, requiredReagent, minPurity);
            if (result.Correct)
                valid++;
            wrong |= result.Wrong;
        }
        return (valid, wrong);
    }

    private (int Valid, bool Wrong) CountPharmaceuticalPills(IEnumerable<EntityUid> contents,
        string requiredReagent, float minPurity)
    {
        var valid = 0;
        var wrong = false;
        foreach (var item in contents)
        {
            if (!HasComp<PillComponent>(item))
                continue;
            var result = InspectMedicine(item, SharedChemMaster.PillSolutionName,
                requiredReagent, minPurity);
            if (result.Correct)
                valid++;
            wrong |= result.Wrong;
        }
        return (valid, wrong);
    }

    private bool IsPillBox(EntityUid uid)
    {
        var id = PharmaceuticalProto(uid);
        return HasComp<ContainerManagerComponent>(uid) &&
            (id.Contains("PillCanister", StringComparison.OrdinalIgnoreCase) ||
             id.Contains("PillBottle", StringComparison.OrdinalIgnoreCase) ||
             id.Contains("PillBox", StringComparison.OrdinalIgnoreCase));
    }

    private PharmaceuticalInspection InspectPharmaceuticalCrate(EntityUid crate, int slot,
        int formulaVariant)
    {
        var contents = PharmaceuticalContents(crate);
        if (contents.Count == 0)
            return new PharmaceuticalInspection(0, false, "Ящик порожній або вміст недоступний");

        var units = 0;
        var required = 1;
        var wrong = false;
        var essentialsMet = true;
        string report;
        var target = OrganizationQuotaRules.PharmaceuticalReagent(formulaVariant);
        var alternate = OrganizationQuotaRules.PharmaceuticalReagent(
            OrganizationQuotaRules.PharmaceuticalSecondaryVariant(formulaVariant));
        var name = OrganizationQuotaRules.PharmaceuticalMedicineName(formulaVariant);
        var alternateName = OrganizationQuotaRules.PharmaceuticalMedicineName(
            OrganizationQuotaRules.PharmaceuticalSecondaryVariant(formulaVariant));
        switch (slot)
        {
            case 0: // Four independent bandage kits, 4 bandages each.
            {
                required = 16;
                var kits = contents.Where(IsPharmaceuticalKit).Take(4).ToArray();
                foreach (var kit in kits)
                    units += PharmaceuticalBandages(kit);
                report = $"Аптечки: {kits.Length}/4; бинти: {units}/16";
                break;
            }
            case 1: // Syringes with one explicitly requested reagent.
            {
                required = 6;
                var syringes = CountPharmaceuticalSyringes(contents, target, 0.90f);
                units = Math.Min(required, syringes.Valid);
                wrong = syringes.Wrong;
                report = $"Шприци ({name}, чистота 90%): {units}/6";
                break;
            }
            case 2: // Pills in a single real pill canister.
            {
                required = 8;
                foreach (var box in contents.Where(IsPillBox))
                {
                    var pills = CountPharmaceuticalPills(PharmaceuticalContents(box), target, 0.95f);
                    units = Math.Max(units, Math.Min(required, pills.Valid));
                    wrong |= pills.Wrong;
                }
                report = $"Пігулки ({name}, чистота 95%) в одній коробці: {units}/8";
                break;
            }
            case 3: // Each kit includes a matched syringe and two different pills.
            {
                required = 28;
                var kits = contents.Where(IsPharmaceuticalKit).Take(4).ToArray();
                var confirmedSyringes = 0;
                var confirmedPills = 0;
                foreach (var kit in kits)
                {
                    var inside = PharmaceuticalContents(kit);
                    var syringes = CountPharmaceuticalSyringes(inside, target, 0.95f);
                    var pills = CountPharmaceuticalPills(inside, alternate, 0.95f);
                    var validSyringes = Math.Min(1, syringes.Valid);
                    var validPills = Math.Min(2, pills.Valid);
                    confirmedSyringes += validSyringes;
                    confirmedPills += validPills;
                    units += PharmaceuticalBandages(kit) + validSyringes + validPills;
                    wrong |= syringes.Wrong || pills.Wrong;
                }
                // Bandages alone can reach >50%, but that is not an actual
                // specialized medicine kit. Require the essential medicines.
                essentialsMet = kits.Length >= 3 && confirmedSyringes >= 2 && confirmedPills >= 4;
                report = $"Аптечки: {kits.Length}/4; склад: {units}/28; шприци {name}: {confirmedSyringes}/4; пігулки {alternateName}: {confirmedPills}/8 (95%)";
                break;
            }
            default:
                return new PharmaceuticalInspection(0, false, "Невідомий тип фармацевтичної квоти");
        }

        if (wrong)
            return new PharmaceuticalInspection(0, true, report + "; помилковий препарат або недостатня чистота");
        if (!essentialsMet)
            return new PharmaceuticalInspection(0, false, report + "; немає обов'язкових медичних компонентів");
        var percentage = Math.Clamp(units * 100 / required, 0, 100);
        return new PharmaceuticalInspection(percentage, false, $"{report}; якість {percentage}%");
    }
}
