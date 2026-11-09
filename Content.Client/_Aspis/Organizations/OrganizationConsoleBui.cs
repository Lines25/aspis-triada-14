using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Client._Triad.Shipyard.Save;
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.Utility;
using Robust.Shared.IoC;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Aspis.Organizations;

[UsedImplicitly]
public sealed class OrganizationConsoleBui(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private OrganizationConsoleWindow? _window;
    private bool _blueprintUploadInProgress;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<OrganizationConsoleWindow>();
        _window.GetOutpostBlueprintFiles = () =>
            EntMan.System<ShipFileManagementSystem>().GetSavedShipFiles()
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .Take(64).ToArray();
        _window.OutpostBlueprintRequested += SubmitOutpostBlueprint;
        _window.FleetBlueprintRequested += SubmitFleetBlueprint;
        _window.FleetRentRequested += id => SendMessage(new OrganizationRentFleetShuttleMessage(id));
        _window.FleetListingRequested += (id, name, description, price) =>
            SendMessage(new OrganizationConfigureFleetListingMessage(id, name, description, price));
        _window.FleetReturnRequested += id => SendMessage(new OrganizationReturnFleetShuttleMessage(id));
        _window.OutpostPurchaseRequested += id => SendMessage(new OrganizationPurchaseOutpostMessage(id));
        _window.OutpostReplacementRequested += id => SendMessage(new OrganizationScheduleOutpostReplacementMessage(id));
        _window.OutpostCancelReplacementRequested += id => SendMessage(new OrganizationCancelOutpostReplacementMessage(id));
        _window.OutpostRedeployRequested += id => SendMessage(new OrganizationRedeployOutpostMessage(id));
        _window.OutpostListingRequested += (org, name, description, publicJoin) =>
            SendMessage(new OrganizationConfigureOutpostListingMessage(org, name, description, publicJoin));
        _window.OutpostToolRequested += (id, tier, configurator) =>
            SendMessage(new OrganizationPrintOutpostToolMessage(id, tier, configurator));
        _window.CreateRequested += (name, kind, industry, privacy, fee, color, investment, a, b, c, d) =>
        {
            var message = new OrganizationCreateMessage(name, kind, industry, privacy, fee, color, investment, a, b, c, d)
            {
                Description = _window?.DraftDescription ?? string.Empty,
                DiscordLink = _window?.DraftDiscordLink ?? string.Empty,
                Banner = _window?.DraftBanner ?? [],
                RosterPublic = _window?.DraftRosterPublic ?? true,
                FactionFocus = _window?.DraftFactionFocus ?? OrganizationFactionFocus.None,
                FactionFoci = _window?.DraftFactionFoci ?? []
            };
            SendMessage(message);
        };
        _window.BannerChooseRequested += ChooseBanner;
        _window.JoinRequested += id => SendMessage(new OrganizationJoinMessage(id));
        _window.ReviewRequested += (id, accept) => SendMessage(new OrganizationReviewMessage(id, accept));
        _window.ConfigureRequested += (id, privacy, fee, color, rosterPublic, name, banner, updateBanner) =>
            SendMessage(new OrganizationConfigureMessage(id, privacy, fee, color)
            { RosterPublic = rosterPublic, Name = name, Banner = banner, UpdateBanner = updateBanner });
        _window.PayrollPolicyRequested += (id, amount, automatic, percent) =>
            SendMessage(new OrganizationPayrollPolicyMessage(id, amount)
            { Automatic = automatic, ProfitPercent = percent });
        _window.SaveCustomRoleRequested += (id, role, title, permissions) =>
            SendMessage(new OrganizationSaveCustomRoleMessage(id, role, title, permissions));
        _window.AssignCustomRoleRequested += (id, member, role) =>
            SendMessage(new OrganizationAssignCustomRoleMessage(id, member, role));
        _window.DevelopmentRequested += (id, branch, tier) =>
            SendMessage(new OrganizationDevelopmentMessage(id, branch) { Tier = tier });
        _window.InvestRequested += (id, scope, focus, amount, target) =>
            SendMessage(new OrganizationInvestMessage(id, scope, focus, amount, target));
        _window.FulfillOrderRequested += id => SendMessage(new OrganizationFulfillOrderMessage(id));
        _window.QuotaClaimRequested += (id, category, slot) =>
            SendMessage(new OrganizationClaimQuotaMessage(id, category, slot));
        _window.QuotaCompleteRequested += (id, category, slot) =>
            SendMessage(new OrganizationCompleteQuotaMessage(id, category, slot));
        _window.QuotaReleaseRequested += (id, category, slot) =>
            SendMessage(new OrganizationReleaseQuotaMessage(id, category, slot));
        _window.ClaimLeadershipRequested += id => SendMessage(new OrganizationClaimLeadershipMessage(id));
        _window.SetDeputyRequested += (org, member) => SendMessage(new OrganizationSetDeputyMessage(org, member));
        _window.SetTitleRequested += (org, slot, title) => SendMessage(new OrganizationSetTitleMessage(org, slot, title));
        _window.SetMemberTitleRequested += (org, member, title) =>
            SendMessage(new OrganizationSetMemberTitleMessage(org, member, title));
        _window.SetOperatingPolicyRequested += (id, policy) =>
            SendMessage(new OrganizationSetOperatingPolicyMessage(id, policy));
        _window.ConfigureBudgetRequested += (org, a, b, c, d) =>
            SendMessage(new OrganizationConfigureBudgetMessage(org, a, b, c, d));
        _window.TransferRequested += (id, amount, deposit) => SendMessage(new OrganizationTreasuryMessage(id, amount, deposit));
        _window.LeaveRequested += () => SendMessage(new OrganizationLeaveMessage());
        _window.SetRoleRequested += (id, member, role) => SendMessage(new OrganizationSetRoleMessage(id, member, role));
        _window.SetPermissionsRequested += (id, role, permissions) =>
            SendMessage(new OrganizationSetPermissionsMessage(id, role, permissions));
        _window.DividendPolicyRequested += (id, percent) =>
            SendMessage(new OrganizationDividendPolicyMessage(id, percent));
        _window.DividendClaimRequested += id =>
            SendMessage(new OrganizationDividendClaimMessage(id));
        _window.LimitOrderRequested += (id, side, count, price) =>
            SendMessage(new OrganizationPlaceLimitOrderMessage(id, side, count, price));
        _window.LimitCancelRequested += (id, order) =>
            SendMessage(new OrganizationCancelLimitOrderMessage(id, order));
        _window.MarketClaimRequested += id =>
            SendMessage(new OrganizationClaimMarketCashMessage(id));
        _window.StockListingRequested += (id, quantity) =>
            SendMessage(new OrganizationStockListingMessage(id, quantity));
        _window.TdfRightsRequested += (member, rights) =>
            SendMessage(new OrganizationTdfRightsMessage(member, rights));
        _window.TdfOpenCaseRequested += source => SendMessage(new OrganizationTdfOpenCaseMessage(source));
        _window.TdfResolveCaseRequested += (id, status, note) =>
            SendMessage(new OrganizationTdfResolveCaseMessage(id, status, note));
        _window.TdfFundRequested += (action, amount) =>
            SendMessage(new OrganizationTdfFundMessage(action, amount));
        _window.StockOrderRequested += (id, order, quantity) =>
            SendMessage(new OrganizationStockMessage(id, order, quantity));
        _window.SetDistinctionRequested += (id, member, distinction) =>
            SendMessage(new OrganizationSetDistinctionMessage(id, member, distinction));
        _window.TransferOwnershipRequested += (id, member) => SendMessage(new OrganizationTransferOwnershipMessage(id, member));
        _window.RemoveMemberRequested += (id, member) => SendMessage(new OrganizationRemoveMemberMessage(id, member));
        _window.ProposeVoteRequested += (id, topic, value) =>
            SendMessage(new OrganizationProposeVoteMessage(id, topic, value));
        _window.CastVoteRequested += (id, vote, approve) =>
            SendMessage(new OrganizationCastVoteMessage(id, vote, approve));
        _window.RefreshRequested += () => SendMessage(new OrganizationRefreshMessage());
        // Resynchronize after the client has constructed its window. The server
        // also sends the private snapshot on BUI-open, but message order between
        // window creation and the opening event can vary across engine versions.
        SendMessage(new OrganizationRefreshMessage());
    }

    // The engine's ordinary state belongs to the shared terminal and is visible
    // to every subscriber. Never accept character-specific data from that path.
    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (message is OrganizationPrivateStateMessage privateState)
            _window?.UpdateState(privateState.State);
    }

    private async Task SendBlueprintInChunks(Guid organization, string name, string envelope,
        OrganizationBlueprintUploadKind kind)
    {
        // Robust client sandbox disallows GZipStream and UTF8Encoding.
        // Transfer the original signed envelope as bounded text fragments.
        // The server verifies the original signature, ownership and hash.
        if (envelope.Length is < 100 or > OrganizationBlueprintUploadRules.MaxEnvelopeCharacters)
            throw new InvalidDataException("Креслення завелике для передачі.");

        var chunkLength = OrganizationBlueprintUploadRules.ChunkCharacters;
        // Do not cut a UTF-16 surrogate pair between network messages; a
        // serializer could otherwise replace the separated halves with '?'.
        // No extra bytes or transcoding are introduced into signed envelopes.
        int NextLength(int start)
        {
            var length = Math.Min(chunkLength, envelope.Length - start);
            if (start + length < envelope.Length &&
                char.IsHighSurrogate(envelope[start + length - 1]))
                length--;
            return length;
        }

        var count = 0;
        for (var position = 0; position < envelope.Length; position += NextLength(position))
            count++;
        if (count is < 1 or > OrganizationBlueprintUploadRules.MaxChunks)
            throw new InvalidDataException("Креслення має забагато частин.");

        var uploadId = Guid.NewGuid();
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            if (_window is not { Disposed: false })
                return;
            var length = NextLength(offset);
            var slice = envelope.Substring(offset, length);
            offset += length;
            SendMessage(new OrganizationBlueprintUploadChunkMessage(organization, name,
                uploadId, kind, index, count, envelope.Length, slice));
            // Spread reliable BUI messages over frames to reduce peak load.
            if ((index + 1) % 4 == 0)
                await Task.Delay(20);
        }
    }

    private async void SubmitFleetBlueprint(Guid organization, string filePath)
    {
        if (_blueprintUploadInProgress)
            return;
        _blueprintUploadInProgress = true;
        try
        {
            var contents = await EntMan.System<ShipFileManagementSystem>().GetShipYamlData(filePath);
            if (_window is not { Disposed: false })
                return;
            if (contents is null || contents.Length is < 100 or > OrganizationBlueprintUploadRules.MaxEnvelopeCharacters)
            {
                _window.SetFleetLocalNotice("Некоректний або завеликий файл шатла (до 12 млн символів).");
                return;
            }
            var leaf = filePath[(filePath.LastIndexOf('/') + 1)..];
            await SendBlueprintInChunks(organization, leaf, contents, OrganizationBlueprintUploadKind.Fleet);
            if (_window is { Disposed: false })
                _window.SetFleetLocalNotice("Креслення передано на серверну перевірку.");
        }
        catch (Exception)
        {
            if (_window is { Disposed: false })
                _window.SetFleetLocalNotice("Не вдалося прочитати або передати креслення шатла.");
        }
        finally
        {
            _blueprintUploadInProgress = false;
        }
    }

    private async void SubmitOutpostBlueprint(Guid organization, string filePath)
    {
        if (_blueprintUploadInProgress)
            return;
        _blueprintUploadInProgress = true;
        try
        {
            var contents = await EntMan.System<ShipFileManagementSystem>().GetShipYamlData(filePath);
            if (_window is not { Disposed: false })
                return;
            if (contents is null || contents.Length is < 100 or > OrganizationBlueprintUploadRules.MaxEnvelopeCharacters)
            {
                _window.SetOutpostLocalNotice("Креслення перевищує ліміт 12 млн символів або пошкоджене.");
                return;
            }
            var leaf = filePath[(filePath.LastIndexOf('/') + 1)..];
            await SendBlueprintInChunks(organization, leaf, contents, OrganizationBlueprintUploadKind.Outpost);
            if (_window is { Disposed: false })
                _window.SetOutpostLocalNotice("Креслення передано на серверну перевірку.");
        }
        catch (Exception)
        {
            if (_window is { Disposed: false })
                _window.SetOutpostLocalNotice("Не вдалося прочитати або передати креслення.");
        }
        finally
        {
            _blueprintUploadInProgress = false;
        }
    }

    private async void ChooseBanner()
    {
        var dialog = IoCManager.Resolve<IFileDialogManager>();
        var filters = new FileDialogFilters(new FileDialogFilters.Group("png"));
        await using var file = await dialog.OpenFile(filters, FileAccess.Read);
        if (file == null || _window is not { Disposed: false })
            return;

        // Validate the IHDR dimensions before decoding. A tiny compressed file
        // can otherwise expand into an enormous image in client memory.
        if (!file.CanSeek || file.Length is < 24 or > 16_384)
        {
            _window.SetBannerError("PNG повинен мати розмір до 16 КБ.");
            return;
        }
        try
        {
            var header = new byte[24];
            file.Position = 0;
            var bytesRead = 0;
            while (bytesRead < header.Length)
            {
                var read = file.Read(header, bytesRead, header.Length - bytesRead);
                if (read == 0)
                    break;
                bytesRead += read;
            }
            if (bytesRead != header.Length ||
                header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71 ||
                header[4] != 13 || header[5] != 10 || header[6] != 26 || header[7] != 10 ||
                header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82)
            {
                _window.SetBannerError("Потрібен коректний файл PNG.");
                return;
            }
            var inputWidth = (uint)(header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19]);
            var inputHeight = (uint)(header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23]);
            if (inputWidth is < 8 or > 32 || inputHeight is < 8 or > 32)
            {
                _window.SetBannerError("PNG має бути від 8×8 до 32×32 пікселів.");
                return;
            }
            file.Position = 0;
            using var picture = Image.Load<Rgba32>(file);
            if (picture.Width is < 8 or > 32 || picture.Height is < 8 or > 32)
            {
                _window.SetBannerError("Розмір зображення має бути від 8×8 до 32×32 пікселів.");
                return;
            }

            // The uploaded PNG is already at most 32x32. Keep its original colors,
            // transparency, and aspect ratio. Do not create or manipulate an
            // ImageSharp canvas: its pixel indexer fails the Robust client sandbox.
            // The visible-pixel check uses Robust's sandbox-compatible pixel span.
            var visiblePixels = 0;
            foreach (var pixel in picture.GetPixelSpan())
            {
                if (pixel.A > 24)
                    visiblePixels++;
            }

            if (visiblePixels == 0)
            {
                _window.SetBannerError("Емблема порожня. Додайте видимі елементи.");
                return;
            }

            // Preserve the original validated PNG, without encoding another
            // image in sandboxed client code. Match the server's 16 KiB limit.
            file.Position = 0;
            var bytes = new byte[(int)file.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = file.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    _window.SetBannerError("Файл PNG прочитано не повністю.");
                    return;
                }
                offset += read;
            }
            _window.SetDraftBanner(bytes);
        }
        catch
        {
            _window?.SetBannerError("Не вдалося прочитати PNG. Перевірте файл.");
        }
    }

    private void SendMessage(BoundUserInterfaceMessage message) => SendPredictedMessage(message);
}
