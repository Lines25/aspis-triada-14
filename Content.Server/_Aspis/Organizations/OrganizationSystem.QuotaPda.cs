using System.Linq;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private void OnOrganizationQuotaPdaRequest(MsgOrganizationQuotaPdaRequest request)
    {
        var response = new MsgOrganizationQuotaPdaResponse();
        // Identity comes from the authenticated channel and the spawned body.
        var session = _players.Sessions.FirstOrDefault(s => s.UserId == request.MsgChannel.UserId);
        if (session?.AttachedEntity is { Valid: true } actor &&
            TryGetCharacter(actor, out var key, out _) && IsOriginalController(actor) &&
            FindMembership(key) is { } org)
        {
            ExpireSectorQuotas();
            response.OrganizationName = org.Name;
            response.Quotas = _sectorQuotaClaims
                .Where(pair => pair.Key.Kind == org.Kind && pair.Value.Organization == org.Id)
                .OrderBy(pair => pair.Value.Deadline)
                .Take(12)
                .Select(pair => new OrganizationPdaQuotaInfo
                {
                    Code = pair.Value.Code,
                    Title = org.Kind == OrganizationKind.Faction
                        ? (pair.Key.Slot > 0
                            ? FactionWreckTitle((OrganizationFactionFocus)pair.Key.Category, pair.Value, pair.Value.FactionScenarioVariant)
                            : FactionAntennaTitle(org, pair.Value.FactionScenarioVariant)) :
                        OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry) ==
                        OrganizationQuotaWorkflow.PharmaceuticalExport
                        ? OrganizationQuotaRules.PharmaceuticalTitle(pair.Key.Slot,
                            pair.Value.PharmaceuticalVariant)
                        : OrganizationQuotaRules.Title(org.Kind, org.Industry,
                            pair.Key.Category, pair.Key.Slot),
                    ProductionRequirements = org.Kind == OrganizationKind.Faction
                        ? (pair.Key.Slot > 0
                            ? FactionWreckDescription((OrganizationFactionFocus)pair.Key.Category, pair.Value, pair.Value.FactionScenarioVariant)
                            : FactionAntennaDescription(org, pair.Value, pair.Value.FactionScenarioVariant))
                        : OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry) ==
                            OrganizationQuotaWorkflow.PharmaceuticalExport
                            ? OrganizationQuotaRules.PharmaceuticalDescription(pair.Key.Slot,
                                pair.Value.Units, pair.Value.PharmaceuticalVariant)
                            : string.Empty,
                    Assignee = pair.Value.AssigneeName,
                    Workflow = org.Kind == OrganizationKind.Faction
                        ? (pair.Key.Slot > 0 ? FactionWreckWorkflow(pair.Value.FactionWreckActivity) : "Планетарна експедиція")
                        : OrganizationQuotaRules.WorkflowName(
                            OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry)),
                    Difficulty = OrganizationQuotaRules.Difficulty(pair.Key.Slot),
                    Delivered = org.Kind == OrganizationKind.Faction
                        ? BuildFactionTaskSteps(org, pair.Value, pair.Key.Slot,
                            pair.Key.Category).Count(step => step.Completed)
                        : pair.Value.Delivered,
                    Total = org.Kind == OrganizationKind.Faction
                        ? BuildFactionTaskSteps(org, pair.Value, pair.Key.Slot,
                            pair.Key.Category).Length : pair.Value.Units,
                    CargoRequestedCrates = pair.Value.CargoRequestedCrates,
                    TimeLimitMinutes = pair.Value.TimeLimitMinutes,
                    PostTargets = (int[]) pair.Value.PostTargets.Clone(),
                    SecondsRemaining = Math.Max(0,
                        (int)(pair.Value.Deadline - _gameTiming.CurTime).TotalSeconds),
                    AssignedToMe = pair.Value.Assignee == key,
                    Completed = pair.Value.Completed,
                    SettlementPending = pair.Value.SettlementPending,
                    FactionSteps = org.Kind == OrganizationKind.Faction
                        ? BuildFactionTaskSteps(org, pair.Value, pair.Key.Slot, pair.Key.Category) : [],
                    IsFactionTask = org.Kind == OrganizationKind.Faction,
                    IsWreckSurvey = org.Kind == OrganizationKind.Faction && pair.Key.Slot > 0,
                    FactionKitOrdered = pair.Value.FactionKitOrdered,
                    FactionKitCanPrint = org.Kind == OrganizationKind.Faction &&
                        pair.Key.Slot == 0 && CanOrderFactionKit(pair.Value),
                    CoordinatesIssued = pair.Value.FactionWreckDisk is { } disk && !Deleted(disk),
                    RouteClosed = pair.Value.FactionWreckReturned,
                    TravelVisited = FactionTravelVisited(pair.Value),
                    QualityPercent = pair.Value.Delivered > 0 ?
                        Math.Clamp(pair.Value.VerifiedQualityTotal / pair.Value.Delivered, 0, 100) : 0,
                    LastInspectionReport = pair.Value.LastInspectionReport,
                    RejectedShipments = pair.Value.RejectedShipments,
                    Posts = (int[])pair.Value.DeliveredByPost.Clone()
                }).ToArray();
        }
        _organizationProfileNet.ServerSendMessage(response, request.MsgChannel);
    }
}
