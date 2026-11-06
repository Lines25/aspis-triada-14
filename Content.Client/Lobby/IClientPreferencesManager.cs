using Content.Shared._Aspis.Organizations;
using Content.Shared.Preferences;

namespace Content.Client.Lobby
{
    public interface IClientPreferencesManager
    {
        event Action OnServerDataLoaded;
        event Action<MsgOrganizationProfileResponse>? OnOrganizationProfileReceived;
        event Action<MsgOrganizationOutpostDirectoryResponse>? OnOrganizationOutpostDirectoryReceived;
        event Action? OnOrganizationFactionTaskCompleted;
        event Action<MsgOrganizationQuotaPdaResponse>? OnOrganizationQuotaPdaReceived;

        bool ServerDataLoaded => Settings != null;

        GameSettings? Settings { get; }
        PlayerPreferences? Preferences { get; }
        void Initialize();
        void RequestOrganizationProfile(int slot);
        void RequestOrganizationOutpostDirectory();
        void SelectCharacter(ICharacterProfile profile);
        void SelectCharacter(int slot);
        void UpdateCharacter(ICharacterProfile profile, int slot);
        void CreateCharacter(ICharacterProfile profile);
        void DeleteCharacter(ICharacterProfile profile);
        void DeleteCharacter(int slot);
    }
}
