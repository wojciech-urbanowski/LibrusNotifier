using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Tests;

public sealed class NotifierCycleTests
{
    [Fact]
    public async Task FirstRunStoresExistingDataWithoutNotifications()
    {
        using var h = new CycleHarness();
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Matematyka", "5"));
        h.Student.Lists[ModuleKind.Messages].Add(Items.Message("Anna", "Stara wiadomość", "1"));

        await h.PingAsync(fullReview: true);

        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.Parent.DetailRequests.Concat(h.Student.DetailRequests));
        await using var db = h.Db.Create();
        Assert.Equal(2, await db.Elements.CountAsync());
        Assert.Equal(0, await db.Notifications.CountAsync());
        Assert.Equal(KindNames.AllModules.Count * 2, await db.Baselines.CountAsync());
        Assert.Equal(KindNames.AllModules, h.Parent.Fetched.Order());
    }

    [Fact]
    public async Task FirstPingOnEmptyDatabaseFetchesAllListsEvenWithoutReview()
    {
        using var h = new CycleHarness();
        await h.PingAsync();
        Assert.Equal(KindNames.AllModules, h.Parent.Fetched.Order());
        Assert.Equal(KindNames.AllModules, h.Student.Fetched.Order());
    }

    [Fact]
    public async Task NewGradeOnBothAccountsGivesOneMailPerGroup()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        var grade = Items.Grade("Matematyka", "5");
        h.Parent.Lists[ModuleKind.Grades].Add(grade);
        h.Student.Lists[ModuleKind.Grades].Add(grade);
        h.Parent.Counters = h.Student.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();

        var parentMail = Assert.Single(h.ParentMails);
        Assert.Equal("[Librus] Nowa ocena: Matematyka – 5", parentMail.Subject);
        Assert.Contains("Widoczne na koncie: rodzica i ucznia", parentMail.Body);
        Assert.Single(h.StudentMails);
        Assert.Equal(2, h.Mail.Sent.Count);

        h.Mail.Sent.Clear();
        await h.PingAsync();
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task NewGradeMailListsTheSubjectGradesNewestFirstWithoutOtherSubjects()
    {
        using var h = new CycleHarness();
        foreach (var grade in new[] { Items.Grade("Matematyka", "4", "2026-10-01"), Items.Grade("Matematyka", "3", "2026-11-05"), Items.Grade("Fizyka", "5", "2026-11-06") })
        {
            h.Parent.Lists[ModuleKind.Grades].Add(grade);
            h.Student.Lists[ModuleKind.Grades].Add(grade);
        }
        await h.BaselineAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var added = Items.Grade("Matematyka", "5", "2026-11-12");
        h.Parent.Lists[ModuleKind.Grades].Add(added);
        h.Student.Lists[ModuleKind.Grades].Add(added);
        h.Parent.Counters = h.Student.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();

        foreach (var mail in new[] { Assert.Single(h.ParentMails), Assert.Single(h.StudentMails) })
        {
            var html = mail.HtmlBody!;
            var report = html[html.IndexOf("<h2", StringComparison.Ordinal)..];
            Assert.Contains("Oceny z przedmiotu Matematyka (od najnowszej)", report);
            Assert.Matches(@"2026-11-12 \(nowa\).*2026-11-05.*2026-10-01", report.Replace("\r", "").Replace("\n", ""));
            Assert.DoesNotContain("2026-11-06", report); // piątka z fizyki
            Assert.Matches(@"(?s)2026-11-12\s+5\s.*\(nowa\).*2026-11-05\s+3\s.*2026-10-01\s+4\s", mail.Body);
            Assert.DoesNotContain("Fizyka", mail.Body.Split("Oceny z przedmiotu")[1]);
        }
    }

    [Fact]
    public async Task CorrectedGradeDisappearsFromTheReportOfTheNewGrade()
    {
        using var h = new CycleHarness();
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Historia", "3", "2026-11-10"));
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Historia", "5", "2026-10-02"));
        await h.BaselineAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        // Nauczyciel poprawił trójkę na czwórkę: w Librusie zostaje jedna ocena z tego dnia.
        h.Parent.Lists[ModuleKind.Grades].RemoveAll(i => i.Fields.Any(f => f is { Label: "Ocena", Value: "3" }));
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Historia", "4", "2026-11-10"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();

        var mail = Assert.Single(h.ParentMails);
        var report = mail.Body[mail.Body.IndexOf("Oceny z przedmiotu", StringComparison.Ordinal)..];
        Assert.Matches(@"(?s)2026-11-10\s+4\s.*\(nowa\).*2026-10-02\s+5\s", report);
        Assert.DoesNotMatch(@"2026-11-10\s+3\s", report); // stara trójka zniknęła z listy w Librusie
        Assert.Equal(2, report.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("2026-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StudentReportDoesNotContainGradesVisibleOnlyOnTheParentAccount()
    {
        using var h = new CycleHarness();
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Chemia", "2", "2026-10-01")); // tylko u rodzica
        await h.BaselineAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var added = Items.Grade("Chemia", "4", "2026-11-12");
        h.Parent.Lists[ModuleKind.Grades].Add(added);
        h.Student.Lists[ModuleKind.Grades].Add(added);
        h.Parent.Counters = h.Student.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();

        Assert.Contains("2026-10-01", Assert.Single(h.ParentMails).Body);
        Assert.DoesNotContain("2026-10-01", Assert.Single(h.StudentMails).Body);
    }

    [Fact]
    public async Task ParentOnlyElementIsNotSentToStudent()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Parent.Lists[ModuleKind.Messages].Add(Items.Message("Wychowawca", "Zebranie", "7"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Messages, 1));

        await h.PingAsync();

        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal([CycleHarness.ParentAddress], mail.To);
        Assert.Contains("Treść wiadomości 7", mail.Body);
        Assert.Single(h.Parent.DetailRequests);
    }

    [Fact]
    public async Task StudentOnlyElementGoesToParentsAndStudent()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Student.Lists[ModuleKind.Announcements].Add(Items.Announcement("Wycieczka"));
        h.Student.Counters = PanelCounters.Of((ModuleKind.Announcements, 1));

        await h.PingAsync();

        Assert.Single(h.ParentMails);
        Assert.Single(h.StudentMails);
        Assert.Contains("Widoczne na koncie: ucznia", h.ParentMails[0].Body);
    }

    [Fact]
    public async Task ElementSeenLaterOnStudentAccountNotifiesOnlyStudent()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        var grade = Items.Grade("Fizyka", "4");
        h.Parent.Lists[ModuleKind.Grades].Add(grade);
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Grades, 1));
        await h.PingAsync();
        Assert.Single(h.ParentMails);
        Assert.Empty(h.StudentMails);

        h.Mail.Sent.Clear();
        h.Parent.Counters = PanelCounters.Empty;
        h.Student.Lists[ModuleKind.Grades].Add(grade);
        h.Student.Counters = PanelCounters.Of((ModuleKind.Grades, 1));
        await h.PingAsync();
        Assert.Empty(h.ParentMails);
        Assert.Single(h.StudentMails);
    }

    [Fact]
    public async Task EditIsNewElementAndDisappearanceIsIgnored()
    {
        using var h = new CycleHarness();
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Historia", "3"));
        await h.BaselineAsync();
        h.Parent.Lists[ModuleKind.Grades].Clear();
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Historia", "4"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();

        Assert.Equal("[Librus] Nowa ocena: Historia – 4", Assert.Single(h.Mail.Sent).Subject);
        await using var db = h.Db.Create();
        Assert.Equal(2, await db.Elements.CountAsync());
    }

    [Fact]
    public async Task OnlyModulesWithNonZeroCountersAreFetched()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Messages, 2), (ModuleKind.Grades, 0));

        await h.PingAsync();

        Assert.Equal([ModuleKind.Messages], h.Parent.Fetched);
        Assert.Empty(h.Student.Fetched);
        Assert.Equal(2, h.Parent.Logins);
    }

    [Fact]
    public async Task FullReviewOnFirstPingThenEveryThirdPingButAtMostHourly()
    {
        using var h = new CycleHarness();
        // Ping 1 o 10:00 jest przeglądem (pierwszy ping po starcie).
        await h.PingAsync();
        Assert.Contains(ModuleKind.Timetable, h.Parent.Fetched);
        var reviews = new List<int>();
        for (var ping = 2; ping <= 9; ping++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(15));
            h.Parent.Fetched.Clear();
            await h.PingAsync();
            if (h.Parent.Fetched.Contains(ModuleKind.Timetable)) reviews.Add(ping);
        }
        // Licznik liczy od przeglądu w pingu 1 (10:00): 4. ping (10:45) ma już 3 pingi od przeglądu, ale
        // minęło dopiero 45 min, więc następny przegląd to ping 5 (11:00), a kolejny ping 9 (12:00).
        Assert.Equal([5, 9], reviews);
    }

    [Fact]
    public async Task FirstPingAfterRestartIsFullReviewEvenWithZeroPanelCounters()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        // Nowość, której licznik panelu nie zgłasza (np. dodana, gdy aplikacja nie działała).
        h.Parent.Lists[ModuleKind.Announcements].Add(Items.Announcement("Zebranie"));

        // Nowy proces = nowy NotifierCycle na tej samej bazie.
        var dispatcher = new NotificationDispatcher(h.Mail, [CycleHarness.ParentAddress], [CycleHarness.StudentAddress], h.Admin, h.Clock, h.Log, h.Stats);
        var restarted = new NotifierCycle([h.Parent, h.Student], h.Db.Create, dispatcher, h.Mail, h.Admin, h.Clock, h.Log, h.Stats, true);

        await restarted.RunAsync(forceFullReview: false, CancellationToken.None);

        Assert.Equal(KindNames.AllModules, h.Parent.Fetched.Order());
        Assert.Equal(KindNames.AllModules, h.Student.Fetched.Order());
        Assert.Contains("Zebranie", Assert.Single(h.ParentMails).Subject);
        Assert.Contains(h.Log.Messages(), m => m.Contains("Ping #1 z przeglądem wszystkich list"));

        // Kolejny ping nie jest już przeglądem.
        h.Parent.Fetched.Clear();
        await restarted.RunAsync(forceFullReview: false, CancellationToken.None);
        Assert.Empty(h.Parent.Fetched);
    }

    [Fact]
    public async Task ReloginDuringCycleAddsModulesFromNewCounters()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Grades, 1));
        h.Parent.EnsureResults.Enqueue(PanelCounters.Of((ModuleKind.Homework, 1)));

        await h.PingAsync();

        Assert.Equal([ModuleKind.Grades, ModuleKind.Homework], h.Parent.Fetched);
    }

    [Fact]
    public async Task AccountErrorDoesNotStopOtherAccountAndIsReportedOncePerDay()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Parent.LoginError = new LibrusApixNet.AuthorizationException("Niepoprawne hasło");
        h.Student.Lists[ModuleKind.Grades].Add(Items.Grade("Chemia", "2"));
        h.Student.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();
        await h.PingAsync();

        Assert.Single(h.StudentMails);
        var admin = Assert.Single(h.AdminMails);
        Assert.Contains("błąd logowania", admin.Subject);
        Assert.Equal(2, h.Parent.Invalidations);
        Assert.Contains(h.Log.Messages(LogLevel.Error), m => m.Contains("Konto rodzic: sprawdzanie przerwane (błąd logowania)"));
    }

    [Fact]
    public async Task FailedModuleIsRetriedInNextPingEvenWithZeroCounters()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Messages, 1), (ModuleKind.Grades, 1));
        h.Parent.FetchErrors[ModuleKind.Grades] = new HttpRequestException("timeout");

        await h.PingAsync();
        Assert.Equal([ModuleKind.Grades], h.Parent.Fetched);

        h.Parent.Fetched.Clear();
        h.Parent.FetchErrors.Clear();
        h.Parent.Counters = PanelCounters.Empty;
        await h.PingAsync();
        Assert.Equal([ModuleKind.Grades, ModuleKind.Messages], h.Parent.Fetched);

        h.Parent.Fetched.Clear();
        await h.PingAsync();
        Assert.Empty(h.Parent.Fetched);
    }

    [Fact]
    public async Task AtMostTwentyMailsPerCycleOldestFirst()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        for (var i = 1; i <= 25; i++) h.Parent.Lists[ModuleKind.Announcements].Add(Items.Announcement($"Ogłoszenie {i:00}"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Announcements, 25));

        await h.PingAsync();
        Assert.Equal(NotificationDispatcher.MaxMailsPerCycle, h.Mail.Sent.Count);
        Assert.EndsWith("Ogłoszenie 01", h.Mail.Sent[0].Subject);

        h.Mail.Sent.Clear();
        h.Parent.Counters = PanelCounters.Empty;
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        await h.PingAsync();
        Assert.Equal(5, h.Mail.Sent.Count);
        Assert.EndsWith("Ogłoszenie 25", h.Mail.Sent[^1].Subject);
    }

    [Fact]
    public async Task OlderPendingNotificationsGoFirst()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Mail.Failure = _ => new MailTransportException("brak sieci", new IOException());
        h.Parent.Lists[ModuleKind.Announcements].Add(Items.Announcement("Pierwsze"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Announcements, 1));
        await h.PingAsync();

        h.Mail.Failure = null;
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        h.Parent.Lists[ModuleKind.Announcements].Add(Items.Announcement("Drugie"));
        await h.PingAsync();

        var subjects = h.ParentMails.Select(m => m.Subject).ToList();
        Assert.Equal(["[Librus] Nowe ogłoszenie: Pierwsze", "[Librus] Nowe ogłoszenie: Drugie"], subjects);
    }

    [Fact]
    public async Task FailedSendStaysPendingAndAdminIsInformedOnce()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Mail.Failure = m => m.To.Contains(CycleHarness.AdminAddress) ? null : new MailTransportException("Nie można połączyć się", new IOException());
        h.Parent.Lists[ModuleKind.Announcements].Add(Items.Announcement("A"));
        h.Parent.Lists[ModuleKind.Announcements].Add(Items.Announcement("B"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Announcements, 2));

        await h.PingAsync();
        h.Parent.Counters = PanelCounters.Empty;
        await h.PingAsync();

        Assert.Empty(h.ParentMails);
        Assert.Single(h.AdminMails);
        await using (var db = h.Db.Create())
        {
            var pending = await db.Notifications.Where(n => n.Status == NotificationStatus.Pending).ToListAsync();
            Assert.Equal(2, pending.Count);
            Assert.Equal(2, pending.Sum(n => n.Attempts));
        }

        h.Mail.Failure = null;
        await h.PingAsync();
        Assert.Equal(2, h.ParentMails.Count);
        await using var check = h.Db.Create();
        Assert.All(await check.Notifications.ToListAsync(), n => Assert.Equal(NotificationStatus.Sent, n.Status));
    }

    [Fact]
    public async Task NoStudentNotificationsWithoutStudentAddresses()
    {
        using var h = new CycleHarness(studentRecipients: false);
        await h.BaselineAsync();
        h.Student.Lists[ModuleKind.Grades].Add(Items.Grade("Biologia", "6"));
        h.Student.Counters = PanelCounters.Of((ModuleKind.Grades, 1));

        await h.PingAsync();

        Assert.Single(h.Mail.Sent);
        await using var db = h.Db.Create();
        Assert.Equal(RecipientGroup.Parents, (await db.Notifications.SingleAsync()).Group);
    }

    [Fact]
    public async Task AccountWhoseFirstRunFailedGetsItsOwnBaselineLater()
    {
        using var h = new CycleHarness();
        h.Student.LoginError = new HttpRequestException("brak sieci");
        h.Student.Lists[ModuleKind.Grades].Add(Items.Grade("Polski", "5"));
        await h.PingAsync(fullReview: true);

        h.Student.LoginError = null;
        h.Mail.Sent.Clear();
        await h.PingAsync();

        Assert.Empty(h.ParentMails);
        Assert.Empty(h.StudentMails);
        await using var db = h.Db.Create();
        Assert.Equal(KindNames.AllModules.Count * 2, await db.Baselines.CountAsync());
    }

    [Fact]
    public async Task DailyStatsCountNewElementsAndMails()
    {
        using var h = new CycleHarness();
        await h.BaselineAsync();
        h.Parent.Lists[ModuleKind.Grades].Add(Items.Grade("Matematyka", "5"));
        h.Parent.Counters = PanelCounters.Of((ModuleKind.Grades, 1));
        await h.PingAsync();

        var stats = h.Stats.Snapshot();
        Assert.Equal(2, stats.Pings);
        Assert.Equal(1, stats.NewElements[ElementTypes.Grade]);
        Assert.Equal(1, stats.MailsSent[RecipientGroup.Parents]);
    }
}
