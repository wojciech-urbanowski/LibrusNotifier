using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;

namespace LibrusNotifier.Reporting;

public sealed record ProblemSummary(LogLevel Level, string Message, int Count, DateTime FirstTime);

public sealed record DailyStatsSnapshot(
    DateOnly Day,
    DateTime StartedLocal,
    int Pings,
    int FullReviews,
    IReadOnlyDictionary<AccountKind, int> Requests,
    IReadOnlyDictionary<AccountKind, int> FailedRequests,
    IReadOnlyDictionary<AccountKind, int> Logins,
    IReadOnlyDictionary<string, int> NewElements,
    int BaselineElements,
    int MovedElements,
    int RemovedElements,
    IReadOnlyDictionary<RecipientGroup, int> MailsSent,
    IReadOnlyDictionary<RecipientGroup, int> DigestsSent,
    int MailFailures,
    int AdminMails,
    IReadOnlyList<ProblemSummary> Problems)
{
    public int TotalRequests => Requests.Values.Sum();
}

/// <summary>Statystyki dnia do podsumowania dla administratorów (bezpieczne wątkowo).</summary>
public sealed class DailyStats(DateOnly day, DateTime startedLocal)
{
    private const int MaxProblems = 100;

    private readonly Lock _gate = new();
    private readonly Dictionary<AccountKind, int> _requests = [];
    private readonly Dictionary<AccountKind, int> _failedRequests = [];
    private readonly Dictionary<AccountKind, int> _logins = [];
    private readonly Dictionary<string, int> _newElements = new(StringComparer.Ordinal);
    private readonly Dictionary<RecipientGroup, int> _mailsSent = [];
    private readonly Dictionary<RecipientGroup, int> _digestsSent = [];
    private readonly List<ProblemSummary> _problems = [];
    private int _pings;
    private int _fullReviews;
    private int _baselineElements;
    private int _movedElements;
    private int _removedElements;
    private int _mailFailures;
    private int _adminMails;

    public void Attach(AppLog log) => log.EntryWritten += entry =>
    {
        if (entry.Level != LogLevel.Info) RecordProblem(entry);
    };

    public void RecordPing(bool fullReview)
    {
        lock (_gate)
        {
            _pings++;
            if (fullReview) _fullReviews++;
        }
    }

    public void RecordRequest(AccountKind account, bool failed)
    {
        lock (_gate)
        {
            _requests[account] = _requests.GetValueOrDefault(account) + 1;
            if (failed) _failedRequests[account] = _failedRequests.GetValueOrDefault(account) + 1;
        }
    }

    public void RecordLogin(AccountKind account)
    {
        lock (_gate) _logins[account] = _logins.GetValueOrDefault(account) + 1;
    }

    public void RecordNewElements(string type, int count)
    {
        if (count == 0) return;
        lock (_gate) _newElements[type] = _newElements.GetValueOrDefault(type) + count;
    }

    /// <summary>Wykryte zmiany terminu i usunięcia wpisów (powiadomienia utworzone w cyklu).</summary>
    public void RecordChanges(int moved, int removed)
    {
        lock (_gate)
        {
            _movedElements += moved;
            _removedElements += removed;
        }
    }

    public void RecordBaseline(int count)
    {
        lock (_gate) _baselineElements += count;
    }

    public void RecordMail(RecipientGroup group, bool success)
    {
        lock (_gate)
        {
            if (success) _mailsSent[group] = _mailsSent.GetValueOrDefault(group) + 1;
            else _mailFailures++;
        }
    }

    /// <summary>Wysłane dzienne zestawienie wydarzeń (nie wlicza się do powiadomień o nowościach).</summary>
    public void RecordDigest(RecipientGroup group)
    {
        lock (_gate) _digestsSent[group] = _digestsSent.GetValueOrDefault(group) + 1;
    }

    public void RecordAdminMail()
    {
        lock (_gate) _adminMails++;
    }

    public void RecordProblem(LogEntry entry)
    {
        lock (_gate)
        {
            var index = _problems.FindIndex(p => p.Level == entry.Level && p.Message == entry.Message);
            if (index >= 0) _problems[index] = _problems[index] with { Count = _problems[index].Count + 1 };
            else if (_problems.Count < MaxProblems) _problems.Add(new ProblemSummary(entry.Level, entry.Message, 1, entry.Time));
        }
    }

    public DailyStatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DailyStatsSnapshot(day, startedLocal, _pings, _fullReviews,
                new Dictionary<AccountKind, int>(_requests), new Dictionary<AccountKind, int>(_failedRequests),
                new Dictionary<AccountKind, int>(_logins), new Dictionary<string, int>(_newElements), _baselineElements, _movedElements, _removedElements,
                new Dictionary<RecipientGroup, int>(_mailsSent), new Dictionary<RecipientGroup, int>(_digestsSent), _mailFailures, _adminMails,
                _problems.ToList());
        }
    }
}
