using System.Net;
using System.Text;
using LibrusNotifier.Configuration;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;
using LibrusNotifier.Reporting;
using LibrusNotifier.Timing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Tests;

internal sealed class FakeClock : Clock
{
    public FakeClock(DateTime warsawLocal) : base(WarsawTime.LoadZone()) => CurrentUtc = ToUtc(warsawLocal);

    public DateTimeOffset CurrentUtc { get; set; }

    public override DateTimeOffset UtcNow => CurrentUtc;

    public void Advance(TimeSpan delta) => CurrentUtc += delta;

    public void SetLocal(DateTime local) => CurrentUtc = ToUtc(local);
}

internal static class Throttles
{
    /// <summary>Bez odstępu między żądaniami (testy niezwiązane z odstępem nie czekają).</summary>
    public static RequestThrottle None(Clock clock) => new(clock, minGap: TimeSpan.Zero, maxGap: TimeSpan.Zero,
        delay: (_, _) => throw new InvalidOperationException("Nieoczekiwane oczekiwanie."));
}

internal sealed class ListLog : IAppLog
{
    public List<LogEntry> Entries { get; } = [];

    public void Write(LogLevel level, string message)
    {
        lock (Entries) Entries.Add(new LogEntry(DateTime.MinValue, level, message));
    }

    public IEnumerable<string> Messages(LogLevel? level = null) => Entries.Where(e => level is null || e.Level == level).Select(e => e.Message);
}

internal sealed class FixedRandom(double value) : Random
{
    public override double NextDouble() => value;
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string Cookie, string UserAgent,
    IReadOnlyDictionary<string, string> Headers);

internal static class TestBrowser
{
    /// <summary>Stały profil Chrome (z nagłówkami Client Hints) dla testów, które nie dotyczą samego losowania.</summary>
    public static readonly BrowserProfile Profile = BrowserCatalog.All().First(p => p.Family == "Chrome" && p.Platform == "Windows");
}

internal sealed class MockHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recorded = new RecordedRequest(request.Method, request.RequestUri!,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : "",
            request.Headers.UserAgent.ToString(),
            request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase));
        Requests.Add(recorded);
        var response = respond(recorded);
        response.RequestMessage = request;
        return response;
    }

    public static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/html") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}

internal sealed class FakeMailSender : IMailSender
{
    public List<OutgoingMail> Sent { get; } = [];
    public Func<OutgoingMail, Exception?>? Failure { get; set; }
    public int Closes { get; private set; }
    public int Attempts { get; private set; }

    public Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        Attempts++;
        if (Failure?.Invoke(mail) is { } error) throw error;
        Sent.Add(mail);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        Closes++;
        return Task.CompletedTask;
    }

    public List<OutgoingMail> To(string address) => Sent.Where(m => m.To.Contains(address)).ToList();
}

internal sealed class FakeGateway(AccountKind account) : IAccountGateway
{
    public AccountKind Account { get; } = account;
    public Dictionary<ModuleKind, List<ObservedItem>> Lists { get; } = KindNames.AllModules.ToDictionary(m => m, _ => new List<ObservedItem>());
    public PanelCounters Counters { get; set; } = PanelCounters.Empty;
    public Exception? LoginError { get; set; }

    /// <summary>Wywoływane na początku logowania, czyli na początku sprawdzania konta w pingu (np. do zapisu kolejności zdarzeń).</summary>
    public Action? OnLogin { get; set; }

    public Dictionary<ModuleKind, Exception> FetchErrors { get; } = [];
    public Queue<PanelCounters?> EnsureResults { get; } = new();
    public List<ModuleKind> Fetched { get; } = [];
    public List<ObservedItem> DetailRequests { get; } = [];
    public int Logins { get; private set; }
    public int Invalidations { get; private set; }

    public Task<PanelCounters> LoginAsync(CancellationToken cancellationToken)
    {
        Logins++;
        OnLogin?.Invoke();
        if (LoginError is not null) throw LoginError;
        return Task.FromResult(Counters);
    }

    public Task<PanelCounters?> EnsureSessionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(EnsureResults.Count > 0 ? EnsureResults.Dequeue() : null);

    public Task<IReadOnlyList<ObservedItem>> FetchListAsync(ModuleKind module, CancellationToken cancellationToken)
    {
        Fetched.Add(module);
        if (FetchErrors.TryGetValue(module, out var error)) throw error;
        return Task.FromResult<IReadOnlyList<ObservedItem>>(Lists[module].ToList());
    }

    public bool HasDetails(ObservedItem item) => LibrusLists.HasDetails(item);

    public Task<IReadOnlyList<Field>?> FetchDetailsAsync(ObservedItem item, CancellationToken cancellationToken)
    {
        DetailRequests.Add(item);
        return Task.FromResult<IReadOnlyList<Field>?>([new Field("Treść", $"Treść wiadomości {item.DetailRef}")]);
    }

    public void Invalidate() => Invalidations++;

    public void Dispose()
    {
    }
}

/// <summary>Tymczasowa baza SQLite w pliku, z zastosowanymi migracjami.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        FilePath = Path.Combine(Path.GetTempPath(), $"librus-test-{Guid.NewGuid():N}.db");
        using var db = Create();
        db.Database.Migrate();
    }

    public string FilePath { get; }

    public NotifierDbContext Create() => NotifierDbContext.Create(FilePath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException)
        {
            // Plik tymczasowy.
        }
    }
}

internal static class Items
{
    public static ObservedItem Grade(string subject, string mark, string date = "2026-11-10", int semester = 1) =>
        LibrusLists.FromGrades(new LibrusApixNet.GradesResult(
            [semester == 1 ? Semester(subject, mark, date, 1) : [], semester == 2 ? Semester(subject, mark, date, 2) : []],
            [], [[], []]))[0];

    private static Dictionary<string, List<LibrusApixNet.Grade>> Semester(string subject, string mark, string date, int semester) =>
        new()
        {
            [subject] = [new LibrusApixNet.Grade(subject, mark, true, date, "/grade/1", $"Ocena: {mark}\nPrzedmiot: {subject}\nKomentarz: dobrze", semester, "Kartkówka", "Anna Nowak", 2)]
        };

    public static ObservedItem Message(string author, string title, string href, string date = "2026-11-10 12:00") =>
        LibrusLists.FromMessages([new LibrusApixNet.Message(author, title, date, href, true, false)])[0];

    public static ObservedItem Announcement(string title) =>
        LibrusLists.FromAnnouncements([new LibrusApixNet.Announcement(title, "Dyrekcja", "Treść ogłoszenia", "2026-11-10")])[0];
}

internal sealed class CycleHarness : IDisposable
{
    public const string ParentAddress = "rodzic@example.com";
    public const string StudentAddress = "uczen@example.com";
    public const string AdminAddress = "admin@example.com";

    public CycleHarness(bool studentRecipients = true, DateTime? start = null)
    {
        Clock = new FakeClock(start ?? new DateTime(2026, 11, 12, 10, 0, 0));
        Stats = new DailyStats(Clock.Today, Clock.Now);
        Admin = new AdminReporter(Mail, [AdminAddress], Log, Stats);
        var dispatcher = new NotificationDispatcher(Mail, [ParentAddress], studentRecipients ? [StudentAddress] : [], Admin, Clock, Log, Stats);
        Cycle = new NotifierCycle([Parent, Student], Db.Create, dispatcher, Mail, Admin, Clock, Log, Stats, studentRecipients);
    }

    public FakeClock Clock { get; }
    public ListLog Log { get; } = new();
    public DailyStats Stats { get; }
    public FakeMailSender Mail { get; } = new();
    public AdminReporter Admin { get; }
    public FakeGateway Parent { get; } = new(AccountKind.Parent);
    public FakeGateway Student { get; } = new(AccountKind.Student);
    public TestDatabase Db { get; } = new();
    public NotifierCycle Cycle { get; }

    public Task PingAsync(bool fullReview = false) => Cycle.RunAsync(fullReview, CancellationToken.None);

    /// <summary>Pierwszy ping na pustej bazie (zapis początkowy wszystkich modułów obu kont).</summary>
    public async Task BaselineAsync()
    {
        await PingAsync(fullReview: true);
        Parent.Fetched.Clear();
        Student.Fetched.Clear();
        Mail.Sent.Clear();
    }

    public List<OutgoingMail> ParentMails => Mail.To(ParentAddress);
    public List<OutgoingMail> StudentMails => Mail.To(StudentAddress);
    public List<OutgoingMail> AdminMails => Mail.To(AdminAddress);

    public void Dispose() => Db.Dispose();
}

internal static class SampleConfig
{
    public const string Text = """
        [rodzic]
        login=LOGIN_RODZICA
        haslo=HASLO_RODZICA

        [uczen]
        login=LOGIN_UCZNIA
        haslo=HASLO_UCZNIA

        [sesja]
        limit_bezczynnosci_minuty=9

        [gmail]
        email=nadawca@gmail.com
        app_password=abcd efgh ijkl mnop

        [odbiorcy_rodzice]
        rodzic@example.com

        [odbiorcy_uczen]
        # Lista może pozostać pusta.
        uczen@example.com

        [odbiorcy_log]
        administrator@example.com

        [kalendarz]
        wojewodztwo=dolnoslaskie
        """;

    public static AppConfig Parse(string text) => ConfigParser.Parse(text, "librus_config.txt");
}
