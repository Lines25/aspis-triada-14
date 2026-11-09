using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>Client asks only for its own active organization's work, not a target org ID.</summary>
public sealed class MsgOrganizationQuotaPdaRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) { }
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) { }
}

public sealed class OrganizationPdaQuotaInfo
{
    public string Code = string.Empty;
    public string Title = string.Empty;
    public string ProductionRequirements = string.Empty;
    public string Assignee = string.Empty;
    public string Workflow = string.Empty;
    public int Difficulty;
    public int Delivered;
    public int Total;
    public int CargoRequestedCrates;
    public int TimeLimitMinutes;
    public int SecondsRemaining;
    public bool AssignedToMe;
    public bool Completed;
    public bool SettlementPending;
    public bool IsFactionTask;
    public bool IsWreckSurvey;
    public bool FactionKitOrdered;
    public bool FactionKitCanPrint;
    public bool CoordinatesIssued;
    public bool RouteClosed;
    public bool TravelVisited;
    public OrganizationFactionStepEntry[] FactionSteps = [];
    public int QualityPercent;
    public string LastInspectionReport = string.Empty;
    public int RejectedShipments;
    public int[] Posts = [0, 0, 0, 0];
    public int[] PostTargets = [0, 0, 0, 0];
}

/// <summary>Print a one-use cargo manifest for the authenticated player's own accepted quota.</summary>
public sealed class MsgOrganizationQuotaChipPrintRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public string Code = string.Empty;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) =>
        Code = buffer.ReadString();
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) =>
        buffer.Write(Code);
}

/// <summary>Bounded read-only PDA snapshot. No hidden member list or bank data.</summary>
public sealed class MsgOrganizationQuotaPdaResponse : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public string OrganizationName = string.Empty;
    public OrganizationPdaQuotaInfo[] Quotas = [];

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        OrganizationName = buffer.ReadString();
        var count = buffer.ReadByte();
        if (count > 12)
        {
            Quotas = [];
            return;
        }
        var quotas = new OrganizationPdaQuotaInfo[count];
        for (var i = 0; i < count; i++)
        {
            var item = new OrganizationPdaQuotaInfo
            {
                Code = buffer.ReadString(),
                Title = buffer.ReadString(),
                ProductionRequirements = buffer.ReadString(),
                Assignee = buffer.ReadString(),
                Workflow = buffer.ReadString(),
                Difficulty = buffer.ReadByte(),
                Delivered = buffer.ReadInt32(),
                Total = buffer.ReadInt32(),
                CargoRequestedCrates = buffer.ReadInt32(),
                TimeLimitMinutes = buffer.ReadInt32(),
                SecondsRemaining = buffer.ReadInt32(),
                AssignedToMe = buffer.ReadByte() != 0,
                Completed = buffer.ReadByte() != 0,
                SettlementPending = buffer.ReadByte() != 0,
                IsFactionTask = buffer.ReadByte() != 0,
                IsWreckSurvey = buffer.ReadByte() != 0,
                FactionKitOrdered = buffer.ReadByte() != 0,
                FactionKitCanPrint = buffer.ReadByte() != 0,
                CoordinatesIssued = buffer.ReadByte() != 0,
                RouteClosed = buffer.ReadByte() != 0,
                TravelVisited = buffer.ReadByte() != 0,
                FactionSteps = ReadFactionSteps(buffer),
                QualityPercent = buffer.ReadByte(),
                LastInspectionReport = buffer.ReadString(),
                RejectedShipments = buffer.ReadInt32()
            };
            for (var post = 0; post < 4; post++)
            {
                item.Posts[post] = buffer.ReadInt32();
                item.PostTargets[post] = buffer.ReadInt32();
            }
            quotas[i] = item;
        }
        Quotas = quotas;
    }

    private static OrganizationFactionStepEntry[] ReadFactionSteps(NetIncomingMessage buffer)
    {
        var count = buffer.ReadByte();
        if (count > 12)
            throw new InvalidOperationException("Invalid faction objective count");
        var steps = new OrganizationFactionStepEntry[count];
        for (var i = 0; i < count; i++)
            steps[i] = new OrganizationFactionStepEntry
            {
                Title = buffer.ReadString(),
                Details = buffer.ReadString(),
                Completed = buffer.ReadByte() != 0
            };
        return steps;
    }

    private static void WriteFactionSteps(NetOutgoingMessage buffer,
        OrganizationFactionStepEntry[]? steps)
    {
        var count = Math.Min(12, steps?.Length ?? 0);
        buffer.Write((byte)count);
        for (var i = 0; i < count; i++)
        {
            var step = steps![i];
            var title = step.Title ?? string.Empty;
            var details = step.Details ?? string.Empty;
            buffer.Write(title.Length > 100 ? title[..100] : title);
            buffer.Write(details.Length > 500 ? details[..500] : details);
            buffer.Write((byte)(step.Completed ? 1 : 0));
        }
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(OrganizationName ?? string.Empty);
        var quotas = Quotas ?? [];
        var count = Math.Min(12, quotas.Length);
        buffer.Write((byte)count);
        for (var i = 0; i < count; i++)
        {
            var item = quotas[i];
            buffer.Write(item.Code);
            buffer.Write(item.Title);
            var requirements = item.ProductionRequirements ?? string.Empty;
            buffer.Write(requirements.Length > 1800 ? requirements[..1800] : requirements);
            buffer.Write(item.Assignee);
            buffer.Write(item.Workflow);
            buffer.Write((byte)Math.Clamp(item.Difficulty, 0, 4));
            buffer.Write(item.Delivered);
            buffer.Write(item.Total);
            buffer.Write(item.CargoRequestedCrates);
            buffer.Write(item.TimeLimitMinutes);
            buffer.Write(item.SecondsRemaining);
            buffer.Write((byte)(item.AssignedToMe ? 1 : 0));
            buffer.Write((byte)(item.Completed ? 1 : 0));
            buffer.Write((byte)(item.SettlementPending ? 1 : 0));
            buffer.Write((byte)(item.IsFactionTask ? 1 : 0));
            buffer.Write((byte)(item.IsWreckSurvey ? 1 : 0));
            buffer.Write((byte)(item.FactionKitOrdered ? 1 : 0));
            buffer.Write((byte)(item.FactionKitCanPrint ? 1 : 0));
            buffer.Write((byte)(item.CoordinatesIssued ? 1 : 0));
            buffer.Write((byte)(item.RouteClosed ? 1 : 0));
            buffer.Write((byte)(item.TravelVisited ? 1 : 0));
            WriteFactionSteps(buffer, item.FactionSteps);
            buffer.Write((byte)Math.Clamp(item.QualityPercent, 0, 100));
            var inspection = item.LastInspectionReport ?? string.Empty;
            buffer.Write(inspection.Length > 160 ? inspection[..160] : inspection);
            buffer.Write(Math.Clamp(item.RejectedShipments, 0, 10000));
            for (var post = 0; post < 4; post++)
            {
                buffer.Write(item.Posts[post]);
                buffer.Write(item.PostTargets[post]);
            }
        }
    }
}

/// <summary>Ask to reprint a lost, server-bound physical coordinates disk.</summary>
public sealed class MsgOrganizationFactionCoordinatesPrintRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public string Code = string.Empty;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) =>
        Code = buffer.ReadString();
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) =>
        buffer.Write(Code);
}
