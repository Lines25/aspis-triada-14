using System;
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

public sealed class MsgOrganizationOutpostDirectoryRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) { }
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) { }
}

public sealed class OrganizationOutpostDirectoryEntry
{
    public NetEntity Station;
    public string Name = string.Empty;
    public string Description = string.Empty;
    public byte[] Banner = [];
    public bool Available;
}

/// <summary>Only public station-facing metadata. Availability is evaluated for the requesting user.</summary>
public sealed class MsgOrganizationOutpostDirectoryResponse : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public OrganizationOutpostDirectoryEntry[] Stations = [];
    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        var count = buffer.ReadByte();
        if (count > 16) return;
        Stations = new OrganizationOutpostDirectoryEntry[count];
        for (var i = 0; i < count; i++)
        {
            var item = new OrganizationOutpostDirectoryEntry
            {
                Station = buffer.ReadNetEntity(),
                Name = buffer.ReadString(),
                Description = buffer.ReadString(),
                Available = buffer.ReadByte() != 0
            };
            var length = buffer.ReadInt32();
            if (length < 0 || length > 16_384)
                return;
            item.Banner = new byte[length];
            for (var j = 0; j < length; j++)
                item.Banner[j] = buffer.ReadByte();
            Stations[i] = item;
        }
    }
    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        var count = Math.Min(16, Stations.Length);
        buffer.Write((byte)count);
        for (var i = 0; i < count; i++)
        {
            var item = Stations[i];
            buffer.Write(item.Station);
            buffer.Write(item.Name ?? string.Empty);
            buffer.Write(item.Description ?? string.Empty);
            buffer.Write((byte)(item.Available ? 1 : 0));
            var banner = item.Banner is { Length: <= 16_384 } ? item.Banner : [];
            buffer.Write(banner.Length);
            for (var j = 0; j < banner.Length; j++)
                buffer.Write(banner[j]);
        }
    }
}
