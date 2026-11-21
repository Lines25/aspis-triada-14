using System;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

[Serializable, NetSerializable]
public enum AspisAgroSprayerVisuals : byte
{
    Loaded
}

[Serializable, NetSerializable]
public enum AspisAgroSprayerVisualLayers : byte
{
    Indicator
}
