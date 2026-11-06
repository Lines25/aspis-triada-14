using Content.Shared._Aspis.Organizations;
using Content.Client.Lobby;
using Robust.Shared.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;

namespace Content.Client._Aspis.Organizations;

/// <summary>PDA read-only quota view. Network messages are registered before connection by ClientPreferencesManager.</summary>
public sealed class OrganizationQuotaPdaSystem : EntitySystem
{
    [Dependency] private IClientNetManager _network = default!;
    [Dependency] private IClientPreferencesManager _preferencesManager = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    private static readonly SoundPathSpecifier CompletedAudio =
        new("/Audio/_Aspis/Organization/mission_passed.ogg");
    public MsgOrganizationQuotaPdaResponse? LastSnapshot { get; private set; }
    public event Action? SnapshotUpdated;

    public override void Initialize()
    {
        base.Initialize();
        _preferencesManager.OnOrganizationQuotaPdaReceived += OnSnapshot;
        _preferencesManager.OnOrganizationFactionTaskCompleted += OnTaskCompleted;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _preferencesManager.OnOrganizationQuotaPdaReceived -= OnSnapshot;
        _preferencesManager.OnOrganizationFactionTaskCompleted -= OnTaskCompleted;
    }

    public void RequestSnapshot() =>
        _network.ClientSendMessage(new MsgOrganizationQuotaPdaRequest());

    public void PrintCargoChip(string code)
    {
        if (code.Length is < 5 or > 48)
            return;
        _network.ClientSendMessage(new MsgOrganizationQuotaChipPrintRequest { Code = code });
        RequestSnapshot();
    }

    public void PrintCoordinatesDisk(string code)
    {
        if (code.Length is < 6 or > 48)
            return;
        _network.ClientSendMessage(new MsgOrganizationFactionCoordinatesPrintRequest { Code = code });
        RequestSnapshot();
    }

    private void OnTaskCompleted() =>
        _audio.PlayGlobal(CompletedAudio, Filter.Local(), false);

    private void OnSnapshot(MsgOrganizationQuotaPdaResponse snapshot)
    {
        LastSnapshot = snapshot;
        SnapshotUpdated?.Invoke();
    }
}
