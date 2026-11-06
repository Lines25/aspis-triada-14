using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Content.Shared.Database;
using Content.Server.Administration.Logs;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.Log;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Persistent TDF review docket. Decisions are documentary only: they do not create
/// wanted records, withdraw funds, punish characters, or rewrite trade history.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const int MaxTdfCases = 160;
    private const int MaxTdfCaseActions = 16;

    // Evidence identifiers are deterministic and survive persistence of the original logs.
    private static string TdfSourceHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    // A client-visible source key must not disclose the trader's private account ID.
    private static string TdfTradeSourceKey(OrganizationTradeRecord t) =>
        "T:" + TdfSourceHash($"{t.Organization:N}:{t.Utc.Ticks}:{t.TraderUserId}:{t.TraderSlot}:" +
            $"{(byte)t.Order}:{t.Quantity}:{t.Price}:{t.Total}");

    private static string TdfConcentrationSourceKey(OrganizationConcentrationRecord a) =>
        "C:" + TdfSourceHash($"{a.Organization:N}:{a.Utc.Ticks}:{a.Company}:{a.Reason}");

    private bool RestoreTdfCases(List<OrganizationTdfCaseRecord>? records)
    {
        _tdfCases.Clear();
        if (records == null)
            return true; // Old Stage 25 registries do not contain this field.
        if (records.Count > MaxTdfCases)
            return false;
        var ids = new HashSet<Guid>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record == null || record.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(record.SourceKey) || record.SourceKey.Length > 220 ||
                record.Organization == Guid.Empty ||
                string.IsNullOrWhiteSpace(record.Company) || record.Company.Length > 64 ||
                string.IsNullOrWhiteSpace(record.Subject) || record.Subject.Length > 128 ||
                string.IsNullOrWhiteSpace(record.Reason) || record.Reason.Length > 320 ||
                record.CreatedUtc == default ||
                !Enum.IsDefined(record.Status) ||
                record.LastOfficer == null || record.LastOfficer.Length > 128 ||
                record.DecisionNote == null || record.DecisionNote.Length > 220 ||
                record.History is null || record.History.Count is < 1 or > MaxTdfCaseActions ||
                !ids.Add(record.Id) || !sources.Add(record.SourceKey))
                return false;
            if (record.History.Any(a => a is null || a.Utc == default ||
                string.IsNullOrWhiteSpace(a.Officer) || a.Officer.Length > 128 ||
                string.IsNullOrWhiteSpace(a.Description) || a.Description.Length > 320))
                return false;
        }
        _tdfCases.AddRange(records);
        return true;
    }

    private bool TryAuthorizeTdfReview(EntityUid console, EntityUid actor, bool resolve, out string officer)
    {
        officer = string.Empty;
        if (!_store.Available || !Authorized(console, actor) ||
            !IsOriginalController(actor) || !IsTdfTerminal(console) ||
            !TryGetCharacter(actor, out var key, out var name) ||
            !TryGetTdfRank(actor, out _, out var grade))
            return false;
        var assigned = (TdfRights(key) & OrganizationTdfServiceRight.Audit) != 0;
        if (grade < (resolve ? 4 : 3) && !assigned)
            return false;
        officer = name;
        return true;
    }

    private void OnTdfOpenCase(EntityUid uid, OrganizationConsoleComponent comp, OrganizationTdfOpenCaseMessage args)
    {
        if (!TryAuthorizeTdfReview(uid, args.Actor, false, out var officer) ||
            string.IsNullOrWhiteSpace(args.SourceKey) || args.SourceKey.Length > 220)
            return;
        if (_tdfCases.Any(c => c.SourceKey == args.SourceKey))
        {
            Refresh(uid, args.Actor, "Цей сигнал уже внесено до справ TDF.");
            return;
        }
        if (_tdfCases.Count >= MaxTdfCases)
        {
            Refresh(uid, args.Actor, "Архів справ TDF заповнений. Потрібне адміністративне обслуговування.");
            return;
        }

        Guid organization;
        string company;
        string subject;
        string reason;
        var trade = _tradeLedger.FirstOrDefault(t => t.Flagged && TdfTradeSourceKey(t) == args.SourceKey);
        if (trade != null)
        {
            organization = trade.Organization;
            company = trade.Company;
            subject = trade.TraderName;
            reason = trade.Reason;
        }
        else
        {
            var concentration = _concentrationAlerts.FirstOrDefault(a => TdfConcentrationSourceKey(a) == args.SourceKey);
            if (concentration == null)
            {
                Refresh(uid, args.Actor, "Вказаний фінансовий сигнал не знайдено в журналі.");
                return;
            }
            organization = concentration.Organization;
            company = concentration.Company;
            subject = "Секторна антимонопольна служба";
            reason = concentration.Reason;
        }
        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(reason))
            return;

        var now = DateTime.UtcNow;
        var docket = new OrganizationTdfCaseRecord
        {
            Id = Guid.NewGuid(), SourceKey = args.SourceKey, Organization = organization,
            Company = company, Subject = subject, Reason = reason,
            CreatedUtc = now, Status = OrganizationTdfCaseStatus.Open,
            LastOfficer = officer,
            History =
            [
                new OrganizationTdfCaseActionRecord
                {
                    Utc = now, Officer = officer, Description = "Відкрито перевірку за фінансовим сигналом."
                }
            ]
        };
        if (!Commit(() => _tdfCases.Add(docket)))
        {
            Refresh(uid, args.Actor, "Не вдалося зберегти справу TDF.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"TDF case OPEN id={docket.Id} source={docket.SourceKey} org={organization} officer={officer}");
        Refresh(uid, args.Actor, "Фінансову перевірку відкрито.");
    }

    private void OnTdfResolveCase(EntityUid uid, OrganizationConsoleComponent comp, OrganizationTdfResolveCaseMessage args)
    {
        if (!TryAuthorizeTdfReview(uid, args.Actor, true, out var officer) ||
            args.Status is not (OrganizationTdfCaseStatus.Cleared or OrganizationTdfCaseStatus.Referred) ||
            args.Note == null)
            return;
        var note = args.Note.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (note.Length is < 5 or > 220 || note.Any(char.IsControl))
        {
            Refresh(uid, args.Actor, "Для рішення TDF потрібне обґрунтування від 5 до 220 символів.");
            return;
        }
        var docket = _tdfCases.FirstOrDefault(c => c.Id == args.CaseId);
        if (docket == null || docket.Status != OrganizationTdfCaseStatus.Open)
        {
            Refresh(uid, args.Actor, "Справу не знайдено або рішення вже ухвалено.");
            return;
        }
        var now = DateTime.UtcNow;
        if (!Commit(() =>
            {
                docket.Status = args.Status;
                docket.LastOfficer = officer;
                docket.DecisionNote = note;
                docket.History.Add(new OrganizationTdfCaseActionRecord
                {
                    Utc = now,
                    Officer = officer,
                    Description = (args.Status == OrganizationTdfCaseStatus.Cleared
                        ? "Справу закрито без підтвердження порушення: "
                        : "Справу направлено на подальший розгляд: ") + note
                });
                if (docket.History.Count > MaxTdfCaseActions)
                    docket.History.RemoveAt(0);
            }))
        {
            Refresh(uid, args.Actor, "Не вдалося зберегти рішення TDF.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.High,
            $"TDF case DECISION id={docket.Id} status={args.Status} officer={officer} note={note}");
        Refresh(uid, args.Actor, "Рішення TDF зареєстровано.");
    }
}
