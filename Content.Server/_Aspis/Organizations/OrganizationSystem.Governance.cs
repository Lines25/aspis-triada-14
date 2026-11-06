using System.Linq;
using Content.Server.Administration.Logs;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;

namespace Content.Server._Aspis.Organizations;

// Voting is a separate corporate authority. It never grants job, treasury,
// ownership transfer, or personnel permissions.
public sealed partial class OrganizationSystem
{
    private const int MaxVoteHistory = 16;
    private const int MaxOpenVotes = 2;
    private const int MaxVoteEligible = 256;
    private static readonly TimeSpan ShareholderVoteDuration = TimeSpan.FromHours(1);

    private static bool IsShareholder(Organization company, CharacterKey key) =>
        company.Shareholders.Any(s => s.UserId == key.UserId && s.Slot == key.Slot && s.Shares > 0);

    private static bool ValidateGovernance(Organization company, OrganizationRecord record)
    {
        if (record.Votes == null || record.Votes.Count > MaxVoteHistory ||
            record.Votes.Any(v => v == null) ||
            record.Votes.Select(v => v.Id).Distinct().Count() != record.Votes.Count)
            return false;

        foreach (var vote in record.Votes)
        {
            if (vote == null || vote.Id == Guid.Empty || !Enum.IsDefined(vote.Topic) ||
                !Enum.IsDefined(vote.State) || vote.CreatedUtc == default ||
                vote.ExpiresUtc <= vote.CreatedUtc ||
                vote.ExpiresUtc - vote.CreatedUtc > ShareholderVoteDuration ||
                (vote.Topic == OrganizationVoteTopic.Dividends &&
                    (vote.Value < 0 || vote.Value > MaxDividendPolicy || vote.Value % 5 != 0)) ||
                (vote.Topic == OrganizationVoteTopic.Strategy &&
                    !Enum.IsDefined((OrganizationOperatingPolicy)vote.Value)) ||
                vote.Eligible == null || vote.Eligible.Count is < 1 or > MaxVoteEligible ||
                vote.Ballots == null || vote.Ballots.Count > vote.Eligible.Count ||
                vote.Eligible.Any(v => v is null || !Guid.TryParse(v.UserId, out _) ||
                    v.Slot is < 0 or >= 256 || v.Shares <= 0 || v.Shares > MaxIssuedShares) ||
                vote.Eligible.Select(v => (v.UserId, v.Slot)).Distinct().Count() != vote.Eligible.Count ||
                vote.Eligible.Sum(v => (long)v.Shares) > company.SharesIssued ||
                vote.Ballots.Any(b => b is null ||
                    !vote.Eligible.Any(v => v.UserId == b.UserId && v.Slot == b.Slot)) ||
                vote.Ballots.Select(b => (b.UserId, b.Slot)).Distinct().Count() != vote.Ballots.Count)
                return false;
        }

        company.Votes.AddRange(record.Votes.Select(v => new OrganizationVoteRecord
        {
            Id = v.Id, Topic = v.Topic, Value = v.Value, State = v.State,
            CreatedUtc = v.CreatedUtc, ExpiresUtc = v.ExpiresUtc,
            Eligible = v.Eligible.Select(e => new OrganizationVoteEligibleRecord
                { UserId = e.UserId, Slot = e.Slot, Shares = e.Shares }).ToList(),
            Ballots = v.Ballots.Select(b => new OrganizationVoteBallotRecord
                { UserId = b.UserId, Slot = b.Slot, Approve = b.Approve }).ToList()
        }));
        return true;
    }

    private static OrganizationVoteRecord CloneVote(OrganizationVoteRecord vote) => new()
    {
        Id = vote.Id,
        Topic = vote.Topic,
        Value = vote.Value,
        State = vote.State,
        CreatedUtc = vote.CreatedUtc,
        ExpiresUtc = vote.ExpiresUtc,
        Eligible = vote.Eligible.Select(v => new OrganizationVoteEligibleRecord
            { UserId = v.UserId, Slot = v.Slot, Shares = v.Shares }).ToList(),
        Ballots = vote.Ballots.Select(b => new OrganizationVoteBallotRecord
            { UserId = b.UserId, Slot = b.Slot, Approve = b.Approve }).ToList()
    };

    private static bool IsVoteOpen(OrganizationVoteRecord vote) =>
        vote.State == OrganizationVoteState.Open && DateTime.UtcNow < vote.ExpiresUtc;

    // Settle unanswered proposals on schedule. Merely rendering an expired vote
    // must not be the only indication that its persisted status has changed.
    private void ExpireOpenShareholderVotes()
    {
        if (!_store.Available)
            return;
        var utcNow = DateTime.UtcNow;
        var expired = _organizations.Values
            .Where(company => company.Kind == OrganizationKind.Company)
            .SelectMany(company => company.Votes.Where(vote =>
                vote.State == OrganizationVoteState.Open && vote.ExpiresUtc <= utcNow)
                .Select(vote => (Company: company.Id, Vote: vote.Id)))
            .ToArray();
        if (expired.Length == 0)
            return;

        // Persist all expirations as one registry transaction. On storage failure
        // the next sweep may retry safely: no money or vote weight is changed.
        if (!Commit(() =>
            {
                foreach (var (companyId, voteId) in expired)
                {
                    if (!_organizations.TryGetValue(companyId, out var company))
                        continue;
                    var vote = company.Votes.FirstOrDefault(v => v.Id == voteId);
                    if (vote?.State == OrganizationVoteState.Open && vote.ExpiresUtc <= utcNow)
                        vote.State = OrganizationVoteState.Expired;
                }
            }))
            return;

        foreach (var (companyId, voteId) in expired)
            _adminLog.Add(LogType.Action, LogImpact.Low,
                $"Shareholder vote expired: company={companyId}, vote={voteId}");
    }

    private static int BallotWeight(OrganizationVoteRecord vote, bool approve) =>
        vote.Ballots.Where(b => b.Approve == approve).Sum(b =>
            vote.Eligible.First(v => v.UserId == b.UserId && v.Slot == b.Slot).Shares);

    private static int EligibleVotes(OrganizationVoteRecord vote) =>
        vote.Eligible.Sum(e => e.Shares);

    private static void SettleVote(Organization company, OrganizationVoteRecord vote)
    {
        if (vote.State != OrganizationVoteState.Open)
            return;
        if (DateTime.UtcNow >= vote.ExpiresUtc)
        {
            vote.State = OrganizationVoteState.Expired;
            return;
        }

        var yes = BallotWeight(vote, true);
        var no = BallotWeight(vote, false);
        var total = EligibleVotes(vote);
        // Approval requires a strict majority of the frozen voting pool.
        // Abstentions never silently turn into approving votes.
        if (yes <= total / 2)
            return;
        vote.State = OrganizationVoteState.Approved;
        if (vote.Topic == OrganizationVoteTopic.Dividends)
            company.DividendRatePercent = vote.Value;
        else if (vote.Topic == OrganizationVoteTopic.Strategy)
            company.OperatingPolicy = (OrganizationOperatingPolicy)vote.Value;
    }

    private void OnProposeShareholderVote(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationProposeVoteMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_store.Available || !_organizations.TryGetValue(args.Organization, out var company) ||
            company.Kind != OrganizationKind.Company || IsTradingSuspended(company) ||
            !IsShareholder(company, key) ||
            !Enum.IsDefined(args.Topic) ||
            (args.Topic == OrganizationVoteTopic.Dividends &&
                (args.Value < 0 || args.Value > MaxDividendPolicy || args.Value % 5 != 0)) ||
            (args.Topic == OrganizationVoteTopic.Strategy &&
                !Enum.IsDefined((OrganizationOperatingPolicy)args.Value)))
            return;

        if (company.Votes.Count(v => IsVoteOpen(v)) >= MaxOpenVotes ||
            company.Votes.Any(v => IsVoteOpen(v) && v.Topic == args.Topic))
        {
            Refresh(uid, actor, "За цим питанням уже триває голосування.");
            return;
        }
        var eligible = company.Shareholders.Where(s => s.Shares > 0).Select(s =>
            new OrganizationVoteEligibleRecord { UserId = s.UserId, Slot = s.Slot, Shares = s.Shares }).ToList();
        if (eligible.Count is < 1 or > MaxVoteEligible)
        {
            Refresh(uid, actor, "Забагато акціонерів для голосування.");
            return;
        }

        var now = DateTime.UtcNow;
        var vote = new OrganizationVoteRecord
        {
            Id = Guid.NewGuid(), Topic = args.Topic, Value = args.Value,
            CreatedUtc = now, ExpiresUtc = now + ShareholderVoteDuration,
            Eligible = eligible
        };
        if (!Commit(() =>
            {
                foreach (var previous in company.Votes.Where(v =>
                             v.State == OrganizationVoteState.Open && v.ExpiresUtc <= now))
                    previous.State = OrganizationVoteState.Expired;
                company.Votes.Add(vote);
                while (company.Votes.Count > MaxVoteHistory)
                {
                    var closed = company.Votes.FindIndex(v => v.State != OrganizationVoteState.Open);
                    if (closed < 0) break;
                    company.Votes.RemoveAt(closed);
                }
            }))
        {
            Refresh(uid, actor, "Не вдалося зберегти голосування.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Shareholder vote opened, company={company.Id}, vote={vote.Id}, topic={vote.Topic}, value={vote.Value}, actor={ToPrettyString(actor):player}");
        Refresh(uid, actor, "Голосування акціонерів відкрито.");
    }

    private void OnCastShareholderVote(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationCastVoteMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_store.Available || !_organizations.TryGetValue(args.Organization, out var company) ||
            company.Kind != OrganizationKind.Company || IsTradingSuspended(company))
            return;
        var vote = company.Votes.FirstOrDefault(v => v.Id == args.Vote);
        if (vote == null || !IsVoteOpen(vote) ||
            !vote.Eligible.Any(e => e.UserId == key.UserId && e.Slot == key.Slot) ||
            vote.Ballots.Any(b => b.UserId == key.UserId && b.Slot == key.Slot))
        {
            Refresh(uid, actor, "Голосувати не можна: голос уже подано або термін завершився.");
            return;
        }

        if (!Commit(() =>
            {
                vote.Ballots.Add(new OrganizationVoteBallotRecord
                    { UserId = key.UserId, Slot = key.Slot, Approve = args.Approve });
                SettleVote(company, vote);
            }))
        {
            Refresh(uid, actor, "Голос не збережено.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Shareholder vote cast, company={company.Id}, vote={vote.Id}, voteApprove={args.Approve}, actor={ToPrettyString(actor):player}");
        Refresh(uid, actor, "Ваш голос зареєстровано.");
    }
}
