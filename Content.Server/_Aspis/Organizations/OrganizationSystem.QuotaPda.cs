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
                            : FactionAntennaTitle(org, pair.Value.FactionScenarioVariant))
                        : org.Industry == OrganizationIndustry.Agriculture
                            ? (pair.Key.Slot == AspisPlanetaryAgriculturalSlot
                                ? "Планетарна агроекспедиція: гербіцид"
                                : AspisAgriculturalCargoTitle(pair.Key.Slot))
                            : QuotaCompanyScenarioTitle(pair.Value.Scenario, org.Industry,
                                pair.Key.Category, pair.Key.Slot, pair.Value.PharmaceuticalVariant),
                    ProductionRequirements = org.Kind == OrganizationKind.Faction
                        ? (pair.Key.Slot > 0
                            ? FactionWreckDescription((OrganizationFactionFocus)pair.Key.Category, pair.Value, pair.Value.FactionScenarioVariant)
                            : FactionAntennaDescription(org, pair.Value, pair.Value.FactionScenarioVariant))
                        : org.Industry == OrganizationIndustry.Agriculture
                            ? (pair.Key.Slot == AspisPlanetaryAgriculturalSlot
                                ? "Забрати колбу з агростанції, заправити встановлений розпилювач і один раз активувати його в атмосфері."
                                : AspisAgriculturalCargoDescription(pair.Key.Slot, pair.Value.Units,
                                    Array.FindIndex(pair.Value.PostTargets, count => count > 0)))
                            : QuotaCompanyScenarioDescription(pair.Value.Scenario, org.Industry,
                                pair.Key.Category, pair.Key.Slot, pair.Value.Units,
                                pair.Value.PharmaceuticalVariant, pair.Value.PostTargets,
                                pair.Value.EstimatedWorkMinutes),
                    Assignee = pair.Value.AssigneeName,
                    Workflow = org.Kind == OrganizationKind.Faction
                        ? (pair.Key.Slot > 0 ? FactionWreckWorkflow(pair.Value.FactionWreckActivity) : "Планетарна експедиція")
                        : org.Industry == OrganizationIndustry.Agriculture && pair.Key.Slot == AspisPlanetaryAgriculturalSlot
                            ? "Планетарна агроекспедиція"
                            : OrganizationQuotaRules.WorkflowName(
                                OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry)),
                    Difficulty = OrganizationQuotaRules.Difficulty(pair.Key.Slot),
                    Delivered = org.Kind == OrganizationKind.Faction
                        ? BuildFactionTaskSteps(org, pair.Value, pair.Key.Slot,
                            pair.Key.Category).Count(step => step.Completed)
                        : org.Industry == OrganizationIndustry.Agriculture && pair.Key.Slot == AspisPlanetaryAgriculturalSlot
                            ? BuildAspisAgroSteps(pair.Value.Code).Count(step => step.Completed)
                            : pair.Value.Delivered,
                    Total = org.Kind == OrganizationKind.Faction
                        ? BuildFactionTaskSteps(org, pair.Value, pair.Key.Slot,
                            pair.Key.Category).Length
                        : org.Industry == OrganizationIndustry.Agriculture && pair.Key.Slot == AspisPlanetaryAgriculturalSlot
                            ? 4 : pair.Value.Units,
                    CargoRequestedCrates = pair.Value.CargoRequestedCrates,
                    TimeLimitMinutes = pair.Value.TimeLimitMinutes,
                    PostTargets = (int[]) pair.Value.PostTargets.Clone(),
                    SecondsRemaining = Math.Max(0,
                        (int)(pair.Value.Deadline - _gameTiming.CurTime).TotalSeconds),
                    AssignedToMe = pair.Value.Assignee == key,
                    Completed = pair.Value.Completed,
                    SettlementPending = pair.Value.SettlementPending,
                    FactionSteps = org.Kind == OrganizationKind.Faction
                        ? BuildFactionTaskSteps(org, pair.Value, pair.Key.Slot, pair.Key.Category)
                        : org.Industry == OrganizationIndustry.Agriculture && pair.Key.Slot == AspisPlanetaryAgriculturalSlot
                            ? BuildAspisAgroSteps(pair.Value.Code) : [],
                    IsFactionTask = org.Kind == OrganizationKind.Faction ||
                        (org.Industry == OrganizationIndustry.Agriculture && pair.Key.Slot == AspisPlanetaryAgriculturalSlot),
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
