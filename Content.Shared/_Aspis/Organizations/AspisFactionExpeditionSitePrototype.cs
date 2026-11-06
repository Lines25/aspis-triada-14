using System;
using System.Collections.Generic;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Shared prototype schema for the faction expedition site catalog.
/// Add a new entry in Resources/Prototypes/_Aspis/Organizations rather than
/// hardcoding a map path in the mission's logic. None are auto-spawned.
/// </summary>
[Prototype("aspisFactionExpeditionSite")]
[Serializable]
public sealed partial class AspisFactionExpeditionSitePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = string.Empty;

    [DataField(required: true)]
    public ResPath GridPath { get; private set; } = default!;

    // A site must be large enough for two well-separated physical objectives.
    // Values are tile-path distances, not a timer or Euclidean straight line.
    [DataField]
    public int MinimumConnectedFloor { get; private set; } = 180;

    [DataField]
    public int MinimumTraversalTiles { get; private set; } = 24;

    [DataField]
    public int HostileCount { get; private set; } = 6;

    [DataField]
    public List<string> HostilePrototypes { get; private set; } = new();

    [DataField]
    public string HazardDescription { get; private set; } = "Невстановлені загрози";
}
