using System;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>Update only the owner's persisted organization description.</summary>
[Serializable, NetSerializable]
public sealed class OrganizationUpdateDescriptionMessage(Guid organization, string description) : BoundUserInterfaceMessage
{
    public readonly Guid Organization = organization;
    public readonly string Description = description;
}

/// <summary>Withdraw an unpublished fleet offer without erasing the signed blueprint.</summary>
[Serializable, NetSerializable]
public sealed class OrganizationClearFleetListingMessage(Guid organization) : BoundUserInterfaceMessage
{
    public readonly Guid Organization = organization;
}
