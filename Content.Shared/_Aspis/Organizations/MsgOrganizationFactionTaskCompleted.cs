using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>Server-authoritative PDA completion notification, sent only after Commit succeeds.</summary>
public sealed class MsgOrganizationFactionTaskCompleted : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) { }
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) { }
}
