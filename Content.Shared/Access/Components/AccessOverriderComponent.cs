using Content.Shared.Access.Systems;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Access.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedAccessOverriderSystem))]
public sealed partial class AccessOverriderComponent : Component
{
    public static string PrivilegedIdCardSlotId = "AccessOverrider-privilegedId";

    [DataField]
    public ItemSlot PrivilegedIdSlot = new();

    [ViewVariables(VVAccess.ReadWrite)]
    [DataField]
    public SoundSpecifier? DenialSound;

    public EntityUid TargetAccessReaderId = new();

    [Serializable, NetSerializable]
    public sealed class WriteToTargetAccessReaderIdMessage : BoundUserInterfaceMessage
    {
        public readonly List<ProtoId<AccessLevelPrototype>> AccessList;

        public WriteToTargetAccessReaderIdMessage(List<ProtoId<AccessLevelPrototype>> accessList)
        {
            AccessList = accessList;
        }
    }

    /// <summary>Corporate permissions use an independent organization-scoped reader, never AccessLevel tags.</summary>
    [Serializable, NetSerializable]
    public sealed class WriteCorporateOutpostTierMessage : BoundUserInterfaceMessage
    {
        public readonly OrganizationOutpostAccessTier Tier;

        public WriteCorporateOutpostTierMessage(OrganizationOutpostAccessTier tier)
        {
            Tier = tier;
        }
    }

    [DataField, AutoNetworkedField]
    public List<ProtoId<AccessLevelPrototype>> AccessLevels = new();

    [ViewVariables(VVAccess.ReadWrite)]
    [DataField]
    public float DoAfter;

    [Serializable, NetSerializable]
    public sealed class AccessOverriderBoundUserInterfaceState : BoundUserInterfaceState
    {
        public readonly bool CorporateMode;
        public readonly bool CorporateTargetValid;
        public readonly OrganizationOutpostAccessTier CorporateCurrentTier;
        public readonly string TargetLabel;
        public readonly Color TargetLabelColor;
        public readonly string PrivilegedIdName;
        public readonly bool IsPrivilegedIdPresent;
        public readonly bool IsPrivilegedIdAuthorized;
        public readonly ProtoId<AccessLevelPrototype>[]? TargetAccessReaderIdAccessList;
        public readonly ProtoId<AccessLevelPrototype>[]? AllowedModifyAccessList;
        public readonly ProtoId<AccessLevelPrototype>[]? MissingPrivilegesList;

        public AccessOverriderBoundUserInterfaceState(bool isPrivilegedIdPresent,
            bool isPrivilegedIdAuthorized,
            ProtoId<AccessLevelPrototype>[]? targetAccessReaderIdAccessList,
            ProtoId<AccessLevelPrototype>[]? allowedModifyAccessList,
            ProtoId<AccessLevelPrototype>[]? missingPrivilegesList,
            string privilegedIdName,
            string targetLabel,
            Color targetLabelColor,
            bool corporateMode = false,
            bool corporateTargetValid = false,
            OrganizationOutpostAccessTier corporateCurrentTier = OrganizationOutpostAccessTier.Member)
        {
            CorporateMode = corporateMode;
            CorporateTargetValid = corporateTargetValid;
            CorporateCurrentTier = corporateCurrentTier;
            IsPrivilegedIdPresent = isPrivilegedIdPresent;
            IsPrivilegedIdAuthorized = isPrivilegedIdAuthorized;
            TargetAccessReaderIdAccessList = targetAccessReaderIdAccessList;
            AllowedModifyAccessList = allowedModifyAccessList;
            MissingPrivilegesList = missingPrivilegesList;
            PrivilegedIdName = privilegedIdName;
            TargetLabel = targetLabel;
            TargetLabelColor = targetLabelColor;
        }
    }

    [Serializable, NetSerializable]
    public enum AccessOverriderUiKey : byte
    {
        Key,
    }
}
