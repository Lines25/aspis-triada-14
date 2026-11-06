using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Read-only request from Character Setup. The server resolves the character by
/// the authenticated channel and the requested slot; clients cannot supply an account ID.
/// </summary>
public sealed class MsgOrganizationProfileRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public int Slot;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) =>
        Slot = buffer.ReadInt32();

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) =>
        buffer.Write(Slot);
}

/// <summary>
/// Public membership information for one of the requesting user's own character slots.
/// No bank balance, account identifier, permissions or shareholder records are transmitted.
/// </summary>
public sealed class MsgOrganizationProfileResponse : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public int Slot;
    public string CharacterName = string.Empty;
    public bool HasMembership;
    public string OrganizationName = string.Empty;
    public string OrganizationKind = string.Empty;
    public string IndustryName = string.Empty;
    public string Position = string.Empty;
    public string ColorHex = "#9AA6B0";
    public byte[] Banner = [];

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        // The client sandbox forbids a banned exception type, so the profile
        // protocol deliberately has no Base64 conversion or exception handler.
        // Symmetric byte operations also avoid raw-stream alignment issues.
        Slot = buffer.ReadInt32();
        CharacterName = buffer.ReadString();
        HasMembership = buffer.ReadByte() != 0;
        OrganizationName = buffer.ReadString();
        OrganizationKind = buffer.ReadString();
        IndustryName = buffer.ReadString();
        Position = buffer.ReadString();
        ColorHex = buffer.ReadString();

        var length = buffer.ReadInt32();
        if (length < 0 || length > 16_384)
        {
            Banner = [];
            return;
        }

        Banner = new byte[length];
        for (var i = 0; i < length; i++)
            Banner[i] = buffer.ReadByte();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Slot);
        buffer.Write(CharacterName ?? string.Empty);
        buffer.Write((byte)(HasMembership ? 1 : 0));
        buffer.Write(OrganizationName ?? string.Empty);
        buffer.Write(OrganizationKind ?? string.Empty);
        buffer.Write(IndustryName ?? string.Empty);
        buffer.Write(Position ?? string.Empty);
        buffer.Write(ColorHex ?? "#9AA6B0");

        var banner = Banner is { Length: > 0 and <= 16_384 } ? Banner : [];
        buffer.Write(banner.Length);
        foreach (var value in banner)
            buffer.Write(value);
    }
}
