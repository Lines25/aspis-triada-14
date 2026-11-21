using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// One stationary physical post can process many successive jobs. Each job has
/// its own authenticated hull/assignment identity and can depart independently.
/// </summary>
public sealed partial class OrganizationSystem
{
    private sealed class ConstructionYardRecord(
        Guid organizationId, Guid assignmentId, string quotaCode, int difficultySlot,
        MapId mapId, Vector2 worldPosition, EntityUid workPlatform, EntityUid contractHull,
        string designName)
    {
        public readonly Guid OrganizationId = organizationId;
        public readonly Guid AssignmentId = assignmentId;
        public readonly string QuotaCode = quotaCode;
        public readonly int DifficultySlot = difficultySlot;
        public readonly MapId MapId = mapId;
        public readonly Vector2 WorldPosition = worldPosition;
        public readonly EntityUid WorkPlatform = workPlatform;
        public readonly EntityUid ContractHull = contractHull;
        public readonly string DesignName = designName;
        public bool Closed;
        public TimeSpan CreatedAt;
        public TimeSpan? CompleteSince;
        public bool Departing;
        public TimeSpan? DepartureStarted;
        public Vector2 DepartureOrigin;
    }

    private readonly Dictionary<Guid, ConstructionYardRecord> _constructionYards = new();
    // Number of LIVE platforms, not number of contracts handled this round.
    private int _constructionYardsCreatedThisRound;
    private readonly Dictionary<EntityUid, TimeSpan> _constructionYardIdleSince = new();

    private bool TryRegisterConstructionYard(
        Guid organizationId, Guid assignmentId, string quotaCode, int slot,
        EntityUid platform, EntityUid hull, Vector2 plannedPosition,
        float footprintRadius, string designName, out string failure)
    {
        failure = string.Empty;
        if (organizationId == Guid.Empty || assignmentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(quotaCode) || slot is < 0 or > 3 ||
            platform == hull || TerminatingOrDeleted(platform) || TerminatingOrDeleted(hull) ||
            !TryComp<MapGridComponent>(platform, out _) ||
            !TryComp<MapGridComponent>(hull, out _) ||
            HasComp<OrganizationConstructionYardComponent>(hull) ||
            _constructionYards.ContainsKey(assignmentId))
        {
            failure = "Недійсна пара платформа/контрактний шатл.";
            return false;
        }
        if (!_organizations.TryGetValue(organizationId, out var org) ||
            org.Kind != OrganizationKind.Company ||
            org.Industry != OrganizationIndustry.Construction ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            failure = "Контракт не належить активній будівельній компанії.";
            return false;
        }
        if (!TryGetConstructionSector(out var sectorMap, out var mapId, out _) ||
            Transform(platform).MapUid != sectorMap ||
            Transform(hull).MapUid != sectorMap ||
            !float.IsFinite(plannedPosition.X) || !float.IsFinite(plannedPosition.Y) ||
            !float.IsFinite(footprintRadius) || footprintRadius is < 1f or > 800f ||
            Vector2.DistanceSquared(Transform(platform).WorldPosition, plannedPosition) > 2f ||
            Vector2.DistanceSquared(Transform(hull).WorldPosition, plannedPosition) >
                footprintRadius * footprintRadius)
        {
            failure = "Шатл або верф не знаходяться в зарезервованому секторі.";
            return false;
        }

        var reusing = TryComp<OrganizationConstructionYardComponent>(platform, out var oldTag);
        if (reusing)
        {
            if (!oldTag!.IsWorkPlatform || oldTag.OrganizationId != organizationId ||
                _constructionYards.Values.Any(y => y.WorkPlatform == platform && !y.Closed))
            {
                failure = "Верф зайнята іншим незавершеним контрактом.";
                return false;
            }
        }
        else
        {
            if (_constructionYardsCreatedThisRound >=
                OrganizationConstructionContractRules.MaximumYardsPerRound)
            {
                failure = "Ліміт одночасно існуючих верфей вичерпано.";
                return false;
            }
            var clearance = MathF.Max(75f, footprintRadius) + ConstructionYardSafetyBuffer;
            foreach (var reservation in _constructionYardReservations)
            {
                if (reservation.MapId == mapId &&
                    Vector2.DistanceSquared(reservation.WorldPosition, plannedPosition) <
                    MathF.Pow(reservation.ClearanceRadius + clearance, 2f))
                {
                    failure = "Місце верфі зайняте.";
                    return false;
                }
            }
        }

        var platformTag = EnsureComp<OrganizationConstructionYardComponent>(platform);
        platformTag.OrganizationId = organizationId;
        platformTag.AssignmentId = assignmentId;
        platformTag.QuotaCode = quotaCode;
        platformTag.IsWorkPlatform = true;
        platformTag.ContractGrid = hull;

        var hullTag = EnsureComp<OrganizationConstructionYardComponent>(hull);
        hullTag.OrganizationId = organizationId;
        hullTag.AssignmentId = assignmentId;
        hullTag.QuotaCode = quotaCode;
        hullTag.IsWorkPlatform = false;
        hullTag.ContractGrid = hull;

        _constructionYards.Add(assignmentId, new ConstructionYardRecord(
            organizationId, assignmentId, quotaCode, slot, mapId,
            plannedPosition, platform, hull, designName)
        {
            CreatedAt = _gameTiming.CurTime
        });
        if (!reusing)
        {
            ReserveConstructionYardPosition(mapId, plannedPosition, footprintRadius);
            _constructionYardsCreatedThisRound++;
        }
        _constructionYardIdleSince.Remove(platform);
        return true;
    }

    private void ClearConstructionYardRoundState()
    {
        // Round-local UIDs are never serialized. The engine handles deletion
        // at restart; no attempt to remove grids underneath the round cleanup.
        _constructionYards.Clear();
        _constructionYardReservations.Clear();
        _constructionYardIdleSince.Clear();
        _recentConstructionVesselModels.Clear();
        _constructionYardsCreatedThisRound = 0;
    }
}
