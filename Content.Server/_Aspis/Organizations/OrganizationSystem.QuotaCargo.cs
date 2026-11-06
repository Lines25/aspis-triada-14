using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Server.Popups;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Cargo.Components;
using Content.Shared.Interaction;
using Content.Shared.Paper;
using Content.Shared.Database;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Server.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Authorized quota manifests: logistics enters Cargo Requests; research,
/// agriculture, mining and manufacturing attach a manifest to a real export crate.
/// Printing or attaching a document never completes a quota.
/// </summary>
public sealed partial class OrganizationSystem
{
    private static readonly SoundSpecifier QuotaManifestPrinterSound =
        new SoundPathSpecifier("/Audio/Machines/printer.ogg");
    private const string QuotaManifestPrototype = "PaperCargoInvoice";
    private readonly Dictionary<CharacterKey, TimeSpan> _quotaManifestPrintCooldowns = new();
    private static readonly TimeSpan QuotaManifestPrintCooldown = TimeSpan.FromSeconds(12);

    [Dependency] private CargoSystem _quotaCargoSystem = default!;
    [Dependency] private PopupSystem _quotaPopup = default!;
    [Dependency] private PaperSystem _quotaPaper = default!;

    private void OnQuotaChipPrintRequested(MsgOrganizationQuotaChipPrintRequest request)
    {
        if (request.Code is not { Length: >= 6 and <= 48 } || !_store.Available)
            return;

        var session = _players.Sessions.FirstOrDefault(s => s.UserId == request.MsgChannel.UserId);
        if (session?.AttachedEntity is not { Valid: true } actor ||
            !TryGetCharacter(actor, out var character, out _) || !IsOriginalController(actor) ||
            FindMembership(character) is not { } org ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;
        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        if (org.Kind == OrganizationKind.Faction)
        {
            TryPrintFactionSupplyInvoice(actor, character, org, request.Code);
            return;
        }
        if (workflow != OrganizationQuotaWorkflow.CargoDelivery && !UsesQuotaExport(workflow))
            return;

        ExpireSectorQuotas();
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == org.Id && c.Code == request.Code && c.Assignee == character &&
            !c.Completed && (workflow != OrganizationQuotaWorkflow.CargoDelivery ||
                c.CargoRequestedCrates < c.Units));
        if (claim == null || _gameTiming.CurTime >= claim.Deadline ||
            (_quotaManifestPrintCooldowns.TryGetValue(character, out var previous) &&
             _gameTiming.CurTime - previous < QuotaManifestPrintCooldown) ||
            !_prototypes.HasIndex<EntityPrototype>(QuotaManifestPrototype))
            return;

        // Physical token uses the existing cargo-invoice prototype until the
        // custom quota-chip sprite/prototype is added to the game's Resources.
        var printed = Spawn(QuotaManifestPrototype, Transform(actor).Coordinates);
        var chip = EnsureComp<OrganizationQuotaChipComponent>(printed);
        chip.Organization = org.Id;
        chip.Code = claim.Code;
        chip.IssuedAt = _gameTiming.CurTime;
        _organizationMetadata.SetEntityName(printed, $"Накладна {claim.Code}");
        if (TryComp<PaperComponent>(printed, out var paper))
            _quotaPaper.SetContent((printed, paper),
                $"НАКЛАДНА НА ВАНТАЖ\nНомер: {claim.Code}\nОрганізація: {org.Name}\nЯщиків: {claim.Units}\n" +
                (workflow == OrganizationQuotaWorkflow.CargoDelivery
                    ? "Вставте накладну в консоль Cargo Requests. Документ одноразовий."
                    : "Прикріпіть накладну до вантажного ящика. Вміст перевіряється при продажу через Cargo."));

        _quotaManifestPrintCooldowns[character] = _gameTiming.CurTime;
        _hands.TryPickupAnyHand(actor, printed);
        _audio.PlayPvs(MoneyConfirmSound, actor);
        _audio.PlayPvs(QuotaManifestPrinterSound, actor);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Organization quota chip printed: code={claim.Code}, user={character.UserId}");
    }

    private void OnInsertQuotaChip(EntityUid console, CargoOrderConsoleComponent component, InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<OrganizationQuotaChipComponent>(args.Used, out var chip))
            return;
        args.Handled = true;
        if (!_store.Available || !TryGetCharacter(args.User, out var character, out _) ||
            !IsOriginalController(args.User) ||
            FindMembership(character) is not { } org || org.Id != chip.Organization ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            _quotaPopup.PopupEntity("Накладна не належить вашій організації.", args.User, args.User);
            return;
        }

        ExpireSectorQuotas();
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == chip.Organization && c.Code == chip.Code && !c.Completed);
        if (claim == null || _gameTiming.CurTime >= claim.Deadline ||
            (org.Kind != OrganizationKind.Faction &&
             OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry) != OrganizationQuotaWorkflow.CargoDelivery) ||
            claim.CargoRequestedCrates >= claim.Units)
        {
            _quotaPopup.PopupEntity("Термін дії накладної завершився або вантаж уже замовлено.", args.User, args.User);
            return;
        }

        if (org.Kind == OrganizationKind.Faction)
        {
            if (claim.FactionWreckMap != null || !CanOrderFactionKit(claim) ||
                !chip.IsFactionFieldSupply ||
                claim.FactionKitInvoice != args.Used ||
                !_quotaCargoSystem.TryQueueOrganizationFactionKit(console, org.Id,
                    claim.Code, character.CharacterName))
            {
                _quotaPopup.PopupEntity("Накладна недійсна, комплект уже замовлено або " +
                    "консоль не належить Trade Mall.", args.User, args.User);
                return;
            }
            claim.FactionKitOrdered = true;
            claim.FactionKitOrders++;
            claim.FactionKitLastOrderedAt = _gameTiming.CurTime;
            claim.FactionKitInvoice = null;
            QueueDel(args.Used);
            _quotaPopup.PopupEntity("Карго прийняло накладну. Комплект поставлено в чергу " +
                "Requests на Trade Mall (потрібне схвалення карго).", args.User, args.User);
            _audio.PlayPvs(MoneyConfirmSound, console);
            return;
        }

        // The four physical trade posts must exist before a delivery is issued.
        // Otherwise a player could pay for a quota they cannot possibly complete.
        if (!_quotaCargoSystem.TryGetQuotaTradeDestination(3, out _))
        {
            _quotaPopup.PopupEntity("Квота недоступна: торгові пости A-D ще не завантажені на мапі.",
                args.User, args.User);
            return;
        }

        // Only printouts issued by THIS server have OrganizationQuotaChipComponent.
        // An ordinary paper with a copied code can never create cargo orders.
        if (!_quotaCargoSystem.TryQueueOrganizationQuotaCrates(console, org.Id, claim.Code,
                character.CharacterName, claim.Units, claim.CargoRequestedCrates, claim.PostTargets, out var queued))
        {
            _quotaPopup.PopupEntity("Карго не приймає накладну. Перевірте Trade Mall, місткість черги та CrateTradeSecureNormal.",
                args.User, args.User);
            return;
        }

        claim.CargoRequestedCrates += queued;
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Quota manifest inserted: code={claim.Code}, user={character.UserId}, crates={queued}");
        _audio.PlayPvs(MoneyConfirmSound, console);
        _quotaPopup.PopupEntity($"Накладну {claim.Code} прийнято. Заявка на {queued} ящиків додана до Requests.",
            args.User, args.User);
        QueueDel(args.Used);
    }
    // The first supply order is available immediately. If the equipment is
    // lost before installation, ONE replacement order is permitted after a
    // 10-minute delay. No repeated free crate generation or mission reset.
    private bool CanOrderFactionKit(SectorQuotaClaim claim)
    {
        if (!claim.Completed && claim.FactionStage == 1 &&
            (claim.FactionFieldKit is not { } missingKit || Deleted(missingKit)))
        {
            claim.FactionFieldKit = null;
            claim.FactionStage = 0;
        }
        if (!claim.Completed && claim.FactionStage is >= 2 and <= 4 &&
            (claim.FactionAntenna is not { } missingAntenna || Deleted(missingAntenna)))
        {
            claim.FactionAntenna = null;
            claim.FactionFieldKit = null;
            claim.FactionStage = 0;
            claim.FactionSciencePrepared = false;
            claim.FactionMilitaryPrepared = false;
            claim.FactionSignalReadyAt = TimeSpan.Zero;
        }
        return claim.FactionStage == 0 && claim.FactionKitOrders < 2 &&
            (claim.FactionFieldKit is not { } activeKit || Deleted(activeKit)) &&
            (claim.FactionKitOrders == 0 ||
             _gameTiming.CurTime - claim.FactionKitLastOrderedAt >= TimeSpan.FromMinutes(10));
    }

    private void TryPrintFactionSupplyInvoice(EntityUid actor, CharacterKey character,
        Organization org, string code)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(QuotaManifestPrototype))
            return;
        ExpireSectorQuotas();
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == org.Id && c.Code == code && c.Assignee == character &&
            c.FactionWreckMap == null && !c.Completed && CanOrderFactionKit(c) &&
            _gameTiming.CurTime < c.Deadline);
        if (claim is null || (_quotaManifestPrintCooldowns.TryGetValue(character, out var printedAt) &&
            _gameTiming.CurTime - printedAt < QuotaManifestPrintCooldown))
            return;
        if (claim.FactionKitInvoice is { } old && !Deleted(old))
            QueueDel(old);
        var invoice = Spawn(QuotaManifestPrototype, Transform(actor).Coordinates);
        var chip = EnsureComp<OrganizationQuotaChipComponent>(invoice);
        chip.Organization = org.Id;
        chip.Code = code;
        chip.IssuedAt = _gameTiming.CurTime;
        chip.IsFactionFieldSupply = true;
        claim.FactionKitInvoice = invoice;
        _organizationMetadata.SetEntityName(invoice, $"Накладна на обладнання {code}");
        if (TryComp<PaperComponent>(invoice, out var paper))
            _quotaPaper.SetContent((invoice, paper),
                $"ПОСТАЧАННЯ ПОЛЬОВОГО ОБЛАДНАННЯ\nФракція: {org.Name}\nЗавдання: {code}\n" +
                "Один комплект. Вставте цей документ у Cargo Requests на Trade Mall.\n" +
                "Карго повинне схвалити замовлення та дочекатися доставки.");
        _quotaManifestPrintCooldowns[character] = _gameTiming.CurTime;
        _hands.TryPickupAnyHand(actor, invoice);
        _audio.PlayPvs(QuotaManifestPrinterSound, actor);
        _quotaPopup.PopupEntity("КПК надрукував накладну. Віднесіть її до Cargo Requests на Trade Mall.",
            actor, actor);
    }

}
