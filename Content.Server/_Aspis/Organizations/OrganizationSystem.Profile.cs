using Content.Shared._Aspis.Organizations;
using Content.Shared.Preferences;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    /// <summary>
    /// No client-selected identity is trusted: the account comes from the network
    /// channel and the name comes from the server's cached character preferences.
    /// The full character key prevents a deleted slot from inheriting an old membership.
    /// </summary>
    private void OnOrganizationProfileRequest(MsgOrganizationProfileRequest request)
    {
        if (request.Slot < 0 || request.Slot >= 64 ||
            !_preferences.TryGetCachedPreferences(request.MsgChannel.UserId, out var preferences) ||
            !preferences.Characters.TryGetValue(request.Slot, out var character) ||
            character is not HumanoidCharacterProfile humanoid)
            return;

        var answer = new MsgOrganizationProfileResponse
        {
            Slot = request.Slot,
            CharacterName = humanoid.Name
        };

        var identity = Key(request.MsgChannel.UserId.ToString(), request.Slot, humanoid.Name);
        if (FindMembership(identity) is { } organization &&
            organization.Members.TryGetValue(identity, out var member))
        {
            answer.HasMembership = true;
            answer.OrganizationName = organization.Name;
            answer.OrganizationKind = organization.Kind == OrganizationKind.Company ? "Компанія" : "Фракція";
            answer.IndustryName = organization.Industry switch
            {
                OrganizationIndustry.None => "Без галузі",
                OrganizationIndustry.Delivery => "Доставка",
                OrganizationIndustry.Trade => "Торгівля",
                OrganizationIndustry.Security => "Охорона",
                OrganizationIndustry.Services => "Обслуговування",
                OrganizationIndustry.Manufacturing => "Виробництво",
                OrganizationIndustry.Research => "Дослідження",
                OrganizationIndustry.Construction => "Будівництво",
                OrganizationIndustry.Medicine => "Фармацевтика",
                OrganizationIndustry.Mining => "Видобуток",
                OrganizationIndustry.Agriculture => "Аграрна справа",
                _ => "Інша"
            };
            answer.Position = MemberTitle(organization, member);
            answer.ColorHex = organization.Color;
            answer.Banner = (byte[])organization.Banner.Clone();
        }

        _organizationProfileNet.ServerSendMessage(answer, request.MsgChannel);
    }
}
