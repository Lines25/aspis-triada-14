using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// The server resolves the company from the authenticated player's saved
/// character slot. A client cannot choose an organization GUID or a station UID.
/// </summary>
public sealed class MsgOrganizationOutpostLobbyRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public int Slot;
    public bool ChangeSelection;
    public bool SelectCompany;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Slot = buffer.ReadInt32();
        ChangeSelection = buffer.ReadByte() != 0;
        SelectCompany = buffer.ReadByte() != 0;
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Slot);
        buffer.Write((byte)(ChangeSelection ? 1 : 0));
        buffer.Write((byte)(SelectCompany ? 1 : 0));
    }
}

/// <summary>Only the requesting character's own available outpost is revealed.</summary>
public sealed class MsgOrganizationOutpostLobbyResponse : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public int Slot;
    public bool Eligible;
    public bool Selected;
    public string StationName = string.Empty;
    public string Reason = string.Empty;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Slot = buffer.ReadInt32();
        Eligible = buffer.ReadByte() != 0;
        Selected = buffer.ReadByte() != 0;
        StationName = buffer.ReadString();
        Reason = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Slot);
        buffer.Write((byte)(Eligible ? 1 : 0));
        buffer.Write((byte)(Selected ? 1 : 0));
        buffer.Write(StationName ?? string.Empty);
        buffer.Write(Reason ?? string.Empty);
    }
}
