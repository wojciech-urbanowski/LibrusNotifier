using System.Net;
using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;

namespace LibrusNotifier.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "librus-tests-" + Guid.NewGuid().ToString("N"));

    public InfrastructureTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void SecondInstanceCannotAcquireLockUntilFirstIsReleased()
    {
        var path = Path.Combine(_directory, "librus_notifier.lock");
        using (var first = InstanceLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(path));
        }
        using var again = InstanceLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public void LogWritesDailyFileRedactsSecretsAndDeletesOldFiles()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var logs = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, AppLog.FileNameFor(new DateOnly(2026, 10, 12))), "stary");
        File.WriteAllText(Path.Combine(logs, AppLog.FileNameFor(new DateOnly(2026, 10, 13))), "30 dni");
        File.WriteAllText(Path.Combine(logs, "inny.txt"), "nie log");
        var log = new AppLog(clock, logs);
        var entries = new List<LogEntry>();
        log.EntryWritten += entries.Add;
        log.AddSecret("TajneHaslo");

        log.Error("Logowanie z hasłem TajneHaslo nie powiodło się");
        var deleted = log.DeleteOldFiles(30);

        var content = File.ReadAllText(Path.Combine(logs, "librus-2026-11-12.log"));
        Assert.Contains("BŁĄD  Logowanie z hasłem *** nie powiodło się", content);
        Assert.DoesNotContain("TajneHaslo", content);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(logs, "librus-2026-10-12.log")));
        Assert.True(File.Exists(Path.Combine(logs, "librus-2026-10-13.log")));
        Assert.True(File.Exists(Path.Combine(logs, "inny.txt")));
        Assert.Equal(LogLevel.Error, Assert.Single(entries).Level);
    }

    [Fact]
    public async Task HolidaysAreFetchedCachedForADayAndCacheIsUsedWhenApiFails()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 1, 0, 0));
        var log = new ListLog();
        var cache = Path.Combine(_directory, "data", "ferie_cache.json");
        var calls = 0;
        var fail = false;
        var handler = new MockHandler(_ =>
        {
            calls++;
            return fail
                ? MockHandler.Html("błąd", HttpStatusCode.ServiceUnavailable)
                : MockHandler.Html("""[{"startDate":"2027-01-18","endDate":"2027-01-31","type":"School","name":[{"language":"PL","text":"Ferie zimowe"}]}]""",
                    mediaType: "application/json");
        });
        var provider = new SchoolHolidayProvider(cache, clock, log, handler);

        var fetched = await provider.LoadAsync("PL-DS", CancellationToken.None);
        Assert.Equal(new SchoolHolidayPeriod(new DateOnly(2027, 1, 18), new DateOnly(2027, 1, 31), "Ferie zimowe"), Assert.Single(fetched));
        Assert.True(File.Exists(cache));
        var query = handler.Requests[0].Uri.Query;
        Assert.Contains("countryIsoCode=PL", query);
        Assert.Contains("subdivisionCode=PL-DS", query);
        Assert.Contains("validFrom=2026-10-12", query);
        Assert.Contains("validTo=2027-11-13", query);

        clock.Advance(TimeSpan.FromHours(20));
        Assert.Single(await provider.LoadAsync("PL-DS", CancellationToken.None));
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromHours(5));
        fail = true;
        Assert.Single(await provider.LoadAsync("PL-DS", CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Contains(log.Messages(LogLevel.Warning), m => m.Contains("używam cache"));

        Assert.Empty(await provider.LoadAsync("PL-MZ", CancellationToken.None));
        Assert.Contains(log.Messages(LogLevel.Warning), m => m.Contains("ferie i wakacje są pomijane"));
    }

    [Fact]
    public async Task PingLoopRunsCyclesInWindowsAndStopsAfterLastWindow()
    {
        using var h = new CycleHarness(start: new DateTime(2026, 11, 14, 6, 0, 0));
        using var guard = new DayGuard(h.Clock, h.Clock.Today, CancellationToken.None);
        var scheduler = new PollingScheduler(h.Clock.Today, PollingPlan.Free, new FixedRandom(0.5));
        var loop = new PingLoop(scheduler, h.Cycle, h.Clock, guard, h.Log, h.Admin, (delay, _) =>
        {
            h.Clock.Advance(delay);
            return Task.CompletedTask;
        });

        await loop.RunAsync();

        // Sobota (dzień wolny, okno 8–22 co 3 h): pingi o 8, 11, 14, 17 i 20; następny wypadłby po 22:00.
        Assert.Equal(5, h.Parent.Logins);
        Assert.Contains(h.Log.Messages(), m => m.Contains("czekam do 08:00"));
        Assert.Contains(h.Log.Messages(), m => m.Contains("Do końca dnia nie ma już terminów"));
        Assert.Equal(new DateTime(2026, 11, 14, 20, 0, 0), h.Clock.Now);
    }

    [Fact]
    public async Task PingLoopEndsOnDayChange()
    {
        using var h = new CycleHarness(start: new DateTime(2026, 11, 12, 23, 0, 0));
        using var guard = new DayGuard(h.Clock, h.Clock.Today, CancellationToken.None);
        var windows = new[] { new PollingWindow("test", TimeSpan.Zero, TimeSpan.FromHours(24), TimeSpan.FromMinutes(50), TimeSpan.Zero, JitterMode.Symmetric) };
        var loop = new PingLoop(new PollingScheduler(h.Clock.Today, windows, new FixedRandom(0.5)), h.Cycle, h.Clock, guard, h.Log, h.Admin,
            (delay, _) =>
            {
                h.Clock.Advance(delay + TimeSpan.FromMinutes(20));
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(loop.RunAsync);
        Assert.True(guard.DayChanged);
        Assert.Equal(1, h.Parent.Logins);
    }

    [Fact]
    public void DailySummaryContainsRequestCountsPerAccountAndErrors()
    {
        var stats = new DailyStats(new DateOnly(2026, 11, 12), new DateTime(2026, 11, 12, 0, 5, 0));
        stats.RecordPing(fullReview: true);
        stats.RecordRequest(AccountKind.Parent, failed: false);
        stats.RecordRequest(AccountKind.Parent, failed: false);
        stats.RecordRequest(AccountKind.Student, failed: true);
        stats.RecordMail(RecipientGroup.Parents, success: true);
        stats.RecordProblem(new LogEntry(new DateTime(2026, 11, 12, 10, 0, 0), LogLevel.Error, "Konto uczeń: błąd"));
        stats.RecordProblem(new LogEntry(new DateTime(2026, 11, 12, 11, 0, 0), LogLevel.Error, "Konto uczeń: błąd"));

        var mail = DailySummary.Build(stats.Snapshot(), "dzień szkolny", "zmiana dnia", new DateTime(2026, 11, 12, 23, 40, 0), 3, ["admin@example.com"]);

        Assert.Equal("[LibrusNotifier] Podsumowanie dnia 2026-11-12 - błędy: 2", mail.Subject);
        Assert.Contains("Podsumowanie dnia 2026-11-12 (czwartek)", mail.Body);
        Assert.Contains("Żądania HTTP do Librusa: 3 (rodzic: 2, uczeń: 1)", mail.Body);
        Assert.Contains("w tym nieudanych: 1", mail.Body);
        Assert.Contains("(×2) Konto uczeń: błąd", mail.Body);
        Assert.Contains("Oczekujące powiadomienia: 3", mail.Body);
    }

    [Fact]
    public async Task AdminReporterSendsSameErrorOnlyOnce()
    {
        var sender = new FakeMailSender();
        var stats = new DailyStats(new DateOnly(2026, 11, 12), new DateTime(2026, 11, 12, 0, 5, 0));
        var reporter = new AdminReporter(sender, ["admin@example.com"], new ListLog(), stats);

        Assert.True(await reporter.ReportCriticalAsync("k1", "błąd logowania", "szczegóły"));
        Assert.False(await reporter.ReportCriticalAsync("k1", "błąd logowania", "szczegóły"));
        Assert.True(await reporter.ReportCriticalAsync("k2", "inny błąd", "szczegóły"));

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("[LibrusNotifier] Błąd: błąd logowania", sender.Sent[0].Subject);
        Assert.Equal(2, stats.Snapshot().AdminMails);
    }

    [Fact]
    public void MailForMessageWithoutDetailsSaysContentUnavailable()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var item = Items.Message("Anna", "Zebranie\nrodziców", "1");
        var element = new ElementEntity
        {
            Type = item.Type,
            Key = "k",
            DataJson = Processing.FieldJson.Serialize(item.Fields),
            VisibleToParent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        Assert.Equal("[Librus] Nowa wiadomość od Anna: Zebranie rodziców", mail.Subject);
        Assert.Contains("Treść niedostępna", mail.Body);
        Assert.Contains("Wykryto: 2026-11-12 10:00", mail.Body);
        Assert.Contains("Treść niedostępna", mail.HtmlBody);
    }

    [Fact]
    public void MailHtmlShowsTimetableChangeAsCard()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var element = new ElementEntity
        {
            Type = ElementTypes.TimetableChange,
            Key = "k",
            DataJson = Processing.FieldJson.Serialize([new Field("Zmiana", "zastępstwo"), new Field("Nauczyciel (zmiana)", "Michalik Aleksandra -> Mirowska Karina")]),
            VisibleToParent = true,
            VisibleToStudent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        var html = Assert.IsType<string>(mail.HtmlBody);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<table "));
        Assert.Matches(@">zastępstwo</span>", html);
        Assert.Contains("Co się zmieniło", html);
        Assert.Contains("<s style=\"color:#777777;\">Michalik Aleksandra</s> -&gt; <strong>Mirowska Karina</strong>", html);
        Assert.Contains("<div>Widoczne na koncie: rodzica i ucznia</div>", html);
        Assert.Contains("<div>Wykryto: 2026-11-12 10:00</div>", html);
        Assert.DoesNotContain("<th", html);
        Assert.Contains("Zmiana: zastępstwo", mail.Body);
    }

    [Fact]
    public void MailHtmlEncodesValuesAndKeepsLineBreaks()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var element = new ElementEntity
        {
            Type = ElementTypes.Message,
            Key = "k",
            DataJson = Processing.FieldJson.Serialize([new Field("Nadawca", "Anna <b>Nowak</b>"), new Field("Temat", "Zebranie")]),
            DetailsJson = Processing.FieldJson.Serialize([new Field("Treść", "Pierwsza linia\r\nDruga & <script>alert(1)</script>")]),
            VisibleToParent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        var html = Assert.IsType<string>(mail.HtmlBody);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("Anna &lt;b&gt;Nowak&lt;/b&gt;", html);
        Assert.Contains("Pierwsza linia<br>", html);
        Assert.Contains("Druga &amp; &lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<table ")); // sama karta z tematem i nadawcą, treść jest pod nią
        Assert.Contains("Druga & <script>alert(1)</script>", mail.Body);
    }

    [Fact]
    public void GmailMessageIsMultipartAlternativeOnlyWhenThereIsHtml()
    {
        var withHtml = GmailSender.BuildMessage("nadawca@example.com", new OutgoingMail(["a@example.com"], "T", "tekst", "<p>html</p>"));
        var alternative = Assert.IsAssignableFrom<MimeKit.Multipart>(withHtml.Body);
        Assert.Equal("alternative", alternative.ContentType.MediaSubtype);
        Assert.Equal("tekst", withHtml.TextBody?.Trim());
        Assert.Equal("<p>html</p>", withHtml.HtmlBody?.Trim());

        var plain = GmailSender.BuildMessage("nadawca@example.com", new OutgoingMail(["a@example.com"], "T", "tekst"));
        Assert.IsType<MimeKit.TextPart>(plain.Body);
        Assert.Null(plain.HtmlBody);
    }
}
