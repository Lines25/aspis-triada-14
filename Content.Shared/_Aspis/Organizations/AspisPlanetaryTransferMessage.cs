using System;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>Server-validated NAV button; never trusts a client-selected map or shuttle UID.</summary>
[Serializable, NetSerializable]
public sealed class AspisPlanetaryTransferMessage : BoundUserInterfaceMessage
{
    public bool ToAir;
    public AspisPlanetaryTransferMessage(bool toAir) => ToAir = toAir;
}
