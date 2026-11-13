using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared.PDA
{
    [Serializable, NetSerializable]
    public sealed class PdaUpdateState : CartridgeLoaderUiState
    {
        public bool FlashlightEnabled;
        public bool HasPen;
        public bool HasPai;
        public bool HasBook;
        public PdaIdInfoText PdaOwnerInfo;
        public string? StationName;
        public bool HasUplink;
        public bool CanPlayMusic;
        public string? Address;
        public int Balance;
        public string? OwnedShipName;
        public TimeSpan? RoundEndTime;
        public string[] OrganizationMembers = [];

        public PdaUpdateState(
            List<NetEntity> programs,
            NetEntity? activeUI,
            bool flashlightEnabled,
            bool hasPen,
            bool hasPai,
            bool hasBook,
            PdaIdInfoText pdaOwnerInfo,
            int balance,
            string? ownedShipName,
            TimeSpan? roundEndTime,
            string? stationName,
            bool hasUplink = false,
            bool canPlayMusic = false,
            string? address = null)
            : base(programs, activeUI)
        {
            FlashlightEnabled = flashlightEnabled;
            HasPen = hasPen;
            HasPai = hasPai;
            HasBook = hasBook;
            PdaOwnerInfo = pdaOwnerInfo;
            HasUplink = hasUplink;
            CanPlayMusic = canPlayMusic;
            StationName = stationName;
            Address = address;
            Balance = balance;
            OwnedShipName = ownedShipName;
            RoundEndTime = roundEndTime;
        }
    }

    [Serializable, NetSerializable]
    public struct PdaIdInfoText
    {
        public string? ActualOwnerName;
        public string? IdOwner;
        public string? JobTitle;
        public string? CompanyName;
        public Color CompanyColor;
        public string? OrganizationName;
        public Color OrganizationColor;
        public string? OrganizationRole;
        public string? StationAlertLevel;
        public Color StationAlertColor;
        public DateTime? CurrentDate;
    }
}
