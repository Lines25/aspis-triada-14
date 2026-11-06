using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Preferences;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;
using Content.Server.Station.Components;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    // A round-local explicit preference, never saved as a permanent permission.
    // Storing the complete character key prevents reuse of a deleted character slot.
    private readonly Dictionary<NetUserId, CharacterKey> _outpostLobbyChoices = new();

    private void OnOrganizationOutpostDirectoryRequest(MsgOrganizationOutpostDirectoryRequest request)
    {
        var session = _players.Sessions.FirstOrDefault(s => s.UserId == request.MsgChannel.UserId);
        HumanoidCharacterProfile? profile = null;
        if (_preferences.TryGetCachedPreferences(request.MsgChannel.UserId, out var preferences) &&
            preferences.Characters.TryGetValue(preferences.SelectedCharacterIndex, out var selected))
            profile = selected as HumanoidCharacterProfile;

        var response = new MsgOrganizationOutpostDirectoryResponse
        {
            Stations = _organizations.Values
                .Where(o => IsValidOutpostHash(o.OutpostActiveHash))
                .OrderBy(o => o.Name)
                .Take(16)
                .Select(o =>
                {
                    var spawned = _outpostStations.TryGetValue(o.Id, out var station) &&
                        !TerminatingOrDeleted(station);
                    return new OrganizationOutpostDirectoryEntry
                    {
                        Station = spawned ? GetNetEntity(station) : NetEntity.Invalid,
                        Name = OutpostStationTitle(o),
                        Description = o.OutpostPublicDescription,
                        Banner = o.Banner is { Length: <= 16_384 } ? o.Banner : [],
                        Available = spawned && session != null && profile != null &&
                            HasComp<StationJobsComponent>(station) &&
                            CanJoinOrganizationOutpost(station, session, profile)
                    };
                }).ToArray()
        };
        _organizationProfileNet.ServerSendMessage(response, request.MsgChannel);
    }

    private void OnOrganizationOutpostLobbyRequest(MsgOrganizationOutpostLobbyRequest request)
    {
        if (request.Slot < 0 || request.Slot >= 64 ||
            !_preferences.TryGetCachedPreferences(request.MsgChannel.UserId, out var preferences) ||
            !preferences.Characters.TryGetValue(request.Slot, out var character) ||
            character is not HumanoidCharacterProfile profile)
            return;

        var key = Key(request.MsgChannel.UserId.ToString(), request.Slot, profile.Name);
        var org = FindMembership(key);
        var eligible = org is { Kind: OrganizationKind.Company,
                                 InsolvencyState: OrganizationInsolvencyState.Active } &&
                       IsValidOutpostHash(org.OutpostActiveHash);

        if (request.ChangeSelection)
        {
            if (request.SelectCompany && eligible)
                _outpostLobbyChoices[request.MsgChannel.UserId] = key;
            else
                _outpostLobbyChoices.Remove(request.MsgChannel.UserId);
        }

        var selected = eligible &&
                       _outpostLobbyChoices.TryGetValue(request.MsgChannel.UserId, out var chosen) &&
                       chosen.Equals(key);
        var answer = new MsgOrganizationOutpostLobbyResponse
        {
            Slot = request.Slot,
            Eligible = eligible,
            Selected = selected,
            StationName = eligible ? OutpostStationTitle(org!) : string.Empty,
            Reason = eligible
                ? "Потрібен перевірений маяк появи. Якщо станція недоступна на початку раунду, буде застосовано звичайний розподіл."
                : "Цей персонаж не має доступного корпоративного аванпосту."
        };
        _organizationProfileNet.ServerSendMessage(answer, request.MsgChannel);
    }

    /// <summary>
    /// Called at round start after outposts have been deployed. This is a
    /// request, not an authorization: membership, selected character and beacon
    /// are checked again immediately before assigning a Contractor slot.
    /// </summary>
    public bool TryGetPreferredRoundStartOutpost(ICommonSession session,
        HumanoidCharacterProfile profile, out EntityUid station)
    {
        station = default;
        if (!_preferences.TryGetCachedPreferences(session.UserId, out var preferences) ||
            !_outpostLobbyChoices.TryGetValue(session.UserId, out var chosen))
            return false;

        var current = Key(session.UserId.ToString(), preferences.SelectedCharacterIndex, profile.Name);
        if (!chosen.Equals(current))
            return false;

        var org = FindMembership(current);
        if (org is not { Kind: OrganizationKind.Company,
                         InsolvencyState: OrganizationInsolvencyState.Active } ||
            !_outpostStations.TryGetValue(org.Id, out var candidate) ||
            TerminatingOrDeleted(candidate) ||
            !HasComp<StationJobsComponent>(candidate) ||
            !TryGetOutpostSpawn(session, profile, candidate, out _))
            return false;

        station = candidate;
        return true;
    }
}
