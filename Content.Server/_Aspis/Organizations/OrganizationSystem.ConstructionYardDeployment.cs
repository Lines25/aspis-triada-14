using System;
using System.Linq;
using System.Numerics;
using Content.Server._NF.Shuttles.Components;
using Content.Server._Mono.ShipRepair;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Aspis.Organizations;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared.Gravity;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.IoC;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Q2.2C.3: reuse a real company-owned construction yard in ordinary sector space.
/// The jobs are real, purchasable civilian Shipyard grids; there is no fabricated
/// procedural replacement ship, isolated map or player-controlled proof of repair.
/// </summary>
public sealed partial class OrganizationSystem
{
    [Dependency] private ShipRepairSystem _constructionShipRepair = default!;
    [Dependency] private DockingSystem _constructionDocking = default!;
    [Dependency] private OrganizationConstructionYardProtectionSystem _constructionProtection = default!;

    private static readonly ResPath ConstructionPostBlueprint =
        new("/Maps/_Aspis/Organizations/aspis_construction_yard.yml");
    private const float ConstructionYardFootprintRadius = 100f;

    private bool TryDeployConstructionYardProbe(
        Guid organizationId, int slot,
        out Vector2 position, out string failure,
        Guid? contractAssignmentId = null, string? contractCode = null)
    {
        position = default;
        failure = string.Empty;
        if (slot is < 0 or > 3)
        {
            failure = "Недопустимий рівень будівельного контракту.";
            return false;
        }
        if (!_organizations.TryGetValue(organizationId, out var company) ||
            company.Kind != OrganizationKind.Company ||
            company.Industry != OrganizationIndustry.Construction ||
            company.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            failure = "Потрібна активна компанія будівельної галузі.";
            return false;
        }
        var assignment = contractAssignmentId ?? Guid.NewGuid();
        var code = contractCode ?? "TEST-YARD-" + assignment.ToString("N")[..8].ToUpperInvariant();
        if (!TryGetConstructionSector(out var sectorMap, out var mapId, out var origin))
        {
            failure = "Не знайдено основної космічної мапи сектора.";
            return false;
        }

        // Reuse a finished company's existing post. Do not spend another map
        // reservation or create another 411-entity platform for every quota.
        var oldPlatform = _constructionYards.Values
            .Where(y => y.OrganizationId == organizationId && y.MapId == mapId &&
                !TerminatingOrDeleted(y.WorkPlatform) &&
                !_constructionYards.Values.Any(other => other.WorkPlatform == y.WorkPlatform && !other.Closed))
            .Select(y => y.WorkPlatform).FirstOrDefault();
        var reused = oldPlatform.Valid;
        if (reused)
            position = Transform(oldPlatform).WorldPosition;
        else if (_constructionYardsCreatedThisRound >= OrganizationConstructionContractRules.MaximumYardsPerRound ||
                 !TryFindConstructionYardPosition(mapId, origin,
                     ConstructionYardFootprintRadius, out position))
        {
            failure = "Немає вільного місця для ще однієї верфі; існуючі пости використовуються повторно.";
            return false;
        }

        EntityUid? platformUid = reused ? oldPlatform : null;
        EntityUid? hullUid = null;
        var registered = false;
        try
        {
            if (!reused)
            {
                if (!_organizationGridLoader.TryLoadGrid(mapId, ConstructionPostBlueprint,
                        out var result) || result is null)
                {
                    failure = "Не вдалося завантажити авторський кресленик верфі.";
                    return false;
                }
                platformUid = result.Value.Owner;
                if (Transform(platformUid.Value).MapUid != sectorMap ||
                    !TryComp<MapGridComponent>(platformUid.Value, out _))
                {
                    failure = "Платформу завантажено поза спільним сектором.";
                    return false;
                }
                _organizationOutpostTransforms.SetWorldRotation(platformUid.Value, Angle.Zero);
                _organizationOutpostTransforms.SetWorldPosition(platformUid.Value, position);

                EnsureComp<ShuttleComponent>(platformUid.Value);
                _organizationShuttle.Disable(platformUid.Value, force: true);
                Comp<ShuttleComponent>(platformUid.Value).Enabled = false;
                EnsureComp<PreventGridAnchorChangesComponent>(platformUid.Value);
                EnsureComp<NoFTLComponent>(platformUid.Value);
                EnsureComp<ShipSavingBlacklistComponent>(platformUid.Value);
                _organizationMetadata.SetEntityName(platformUid.Value, "Секторна верф");

                var postGravity = EnsureComp<GravityComponent>(platformUid.Value);
                postGravity.Inherent = true;
                postGravity.EnabledVV = true;

                var affiliation = EnsureComp<OrganizationShipIFFComponent>(platformUid.Value);
                affiliation.OrganizationId = company.Id.ToString("N");
                affiliation.OrganizationName = company.Name;
                Dirty(platformUid.Value, affiliation);
                _organizationShuttle.SetIFFColor(platformUid.Value, Color.FromHex(company.Color));
                _organizationShuttle.SetIFFReadOnly(platformUid.Value, true);
                if (!_constructionProtection.Protect(platformUid.Value))
                {
                    failure = "Не вдалося захистити конструкції стаціонарної верфі.";
                    return false;
                }
            }

            // Load an authentic civilian Shipyard vessel on THIS map. Select a
            // compatible south-facing port geometrically, not by editing the
            // vessel's original map or making up a replacement docking hatch.
            if (!TryLoadConstructionShipyardVessel(mapId, platformUid!.Value, slot,
                    assignment, out var loadedHull, out var northPort,
                    out var shipPort, out var vesselName, out var shortName, out failure))
                return false;
            hullUid = loadedHull;
            if (Transform(hullUid.Value).MapUid != sectorMap)
            {
                failure = "Контрактний шатл потрапив не на секторну мапу.";
                return false;
            }

            EnsureComp<ShuttleComponent>(hullUid.Value);
            _organizationShuttle.Disable(hullUid.Value, force: true);
            Comp<ShuttleComponent>(hullUid.Value).Enabled = false;
            EnsureComp<PreventGridAnchorChangesComponent>(hullUid.Value);
            EnsureComp<NoFTLComponent>(hullUid.Value);
            EnsureComp<ShipSavingBlacklistComponent>(hullUid.Value);
            var gravity = EnsureComp<GravityComponent>(hullUid.Value);
            gravity.Inherent = true;
            gravity.EnabledVV = true;

            // Snapshot the FULL, authentic ship before controlled blast damage.
            // ShipRepairTool can restore both missing plating and captured
            // equipment, including structures made repairable for this job.
            if (!TryDamageConstructionShipyardVessel(hullUid.Value,
                    shipPort.Owner, slot, assignment, out failure))
                return false;

            // This dock is NEVER damaged. The hull and post both remain static
            // until the quota is completed, then the empty ship undocks and
            // physically flies out of the sector before removal.
            _constructionDocking.Dock(northPort, shipPort);
            _organizationMetadata.SetEntityName(hullUid.Value, "Ремонт: " + shortName);
            _organizationShuttle.SetIFFColor(hullUid.Value, Color.White);
            _organizationShuttle.SetIFFReadOnly(hullUid.Value, true);

            if (!TryRegisterConstructionYard(organizationId, assignment, code, slot,
                    platformUid.Value, hullUid.Value, position,
                    ConstructionYardFootprintRadius, vesselName, out failure))
                return false;

            _constructionYardIdleSince.Remove(platformUid.Value);
            registered = true;
            // Only this company's physical platform can issue its contract tools.
            // A missing tool must never invalidate a successfully docked ship.
            try
            {
                EnsureConstructionYardWorkTools(platformUid.Value, organizationId);
            }
            catch (Exception ex)
            {
                _log.GetSawmill("organizations.construction-yards")
                    .Warning($"Unable to issue construction tools: {ex.Message}");
            }
            return true;
        }
        catch (Exception ex)
        {
            failure = $"Створення реального контрактного шатла завершилося помилкою ({ex.GetType().Name}).";
            _log.GetSawmill("organizations.construction-yards")
                .Error($"Real shipyard vessel deployment failed: {ex}");
            return false;
        }
        finally
        {
            if (!registered)
            {
                if (hullUid is { } hull && !TerminatingOrDeleted(hull))
                    QueueDel(hull);
                if (!reused && platformUid is { } post && !TerminatingOrDeleted(post))
                    QueueDel(post);
            }
        }
    }
}
