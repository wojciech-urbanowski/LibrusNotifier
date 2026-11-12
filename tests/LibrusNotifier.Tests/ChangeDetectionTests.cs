using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Tests;

internal static class ScheduleItems
{
    /// <summary>Wpis terminarza z listopada 2026 (jak z listy Librusa): sprawdzian z opisem i numerem lekcji.</summary>
    public static ObservedItem Test(int day, string subject, string description, int lesson, string title = "Sprawdzian") =>
        LibrusLists.FromSchedule(2026, 11, new Dictionary<int, List<LibrusApixNet.Event>>
        {
            [day] =
            [
                new LibrusApixNet.Event(title, subject, new Dictionary<string, string> { ["Opis"] = description, ["Nauczyciel"] = "Selwesiuk Jolanta" },
                    day.ToString(), lesson, "unknown", "/terminarz/szczegoly/1")
            ]
        })[0];

    public static void Set(FakeGateway gateway, params ObservedItem[] items)
    {
        gateway.Lists[ModuleKind.Schedule].Clear();
        gateway.Lists[ModuleKind.Schedule].AddRange(items);
    }
}

public sealed class ChangeDetectorTests : IDisposable
{
    private static readonly DateTime EarlyUtc = new(2026, 11, 12, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SeenUtc = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FetchUtc = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _db = new();
    private readonly ListLog _log = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 12, 10, 0, 0)); // 09:00 UTC

    public void Dispose() => _db.Dispose();

    private void Fetch(AccountKind account, ModuleKind module, DateTime utc)
    {
        using var db = _db.Create();
        var existing = db.Baselines.FirstOrDefault(b => b.Account == account && b.Module == module);
        if (existing is null) db.Baselines.Add(new BaselineEntity { Account = account, Module = module, CompletedUtc = EarlyUtc, LastFetchUtc = utc });
        else existing.LastFetchUtc = utc;
        db.SaveChanges();
    }

    private void FetchAll(ModuleKind module, DateTime parentUtc, DateTime studentUtc)
    {
        Fetch(AccountKind.Parent, module, parentUtc);
        Fetch(AccountKind.Student, module, studentUtc);
    }

    private ElementEntity Add(ElementEntity element, params (RecipientGroup Group, NotificationStatus Status)[] notifications)
    {
        foreach (var (group, status) in notifications)
            element.Notifications.Add(new NotificationEntity { Group = group, Status = status, CreatedUtc = EarlyUtc });
        using var db = _db.Create();
        db.Elements.Add(element);
        db.SaveChanges();
        return element;
    }

    private static ElementEntity Test(string date, string description = "Test 2", string subject = "Język niemiecki", string lesson = "4",
        DateTime? lastSeen = null, bool parent = true, bool student = true) =>
        DigestElements.Schedule(date, "Sprawdzian", subject, lesson, "Selwesiuk Jolanta", description, parent: parent, student: student, lastSeen: lastSeen ?? SeenUtc);

    private async Task<ChangeDetectionResult> DetectAsync(bool studentNotifications = true)
    {
        await using var db = _db.Create();
        return await new ChangeDetector(db, _clock, _log, studentNotifications).DetectAsync(CancellationToken.None);
    }

    private List<NotificationEntity> Notifications()
    {
        using var db = _db.Create();
        return db.Notifications.AsNoTracking().OrderBy(n => n.Id).ToList();
    }

    private ElementEntity Reload(ElementEntity element)
    {
        using var db = _db.Create();
        return db.Elements.AsNoTracking().Single(e => e.Id == element.Id);
    }

    [Fact]
    public async Task GoneFutureElementGetsRemovedNotificationForParentsAndStudent()
    {
        var gone = Add(Test("2026-11-20"), (RecipientGroup.Parents, NotificationStatus.Sent));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(2, result.Removed);
        var removed = Notifications().Where(n => n.Kind == NotificationKind.Removed).ToList();
        Assert.Equal([RecipientGroup.Parents, RecipientGroup.Student], removed.Select(n => n.Group).Order());
        Assert.All(removed, n =>
        {
            Assert.Equal(gone.Id, n.ElementId);
            Assert.Equal(NotificationStatus.Pending, n.Status);
            Assert.Null(n.RelatedElementId);
        });
        Assert.NotNull(Reload(gone).GoneHandledUtc);
    }

    [Fact]
    public async Task ElementOnLatestListsIsNotGone()
    {
        Add(Test("2026-11-20", lastSeen: FetchUtc));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        Assert.False((await DetectAsync()).Any);
        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task ElementVisibleToBothWaitsUntilBothAccountsRefetchedTheList()
    {
        var element = Add(Test("2026-11-20"));
        FetchAll(ModuleKind.Schedule, FetchUtc, EarlyUtc); // uczeń nie pobrał listy od ostatniego zobaczenia elementu

        Assert.False((await DetectAsync()).Any);
        Assert.Null(Reload(element).GoneHandledUtc);

        Fetch(AccountKind.Student, ModuleKind.Schedule, FetchUtc.AddMinutes(3));
        Assert.True((await DetectAsync()).Removed > 0);
    }

    [Fact]
    public async Task ParentOnlyElementIsNotGoneBecauseStudentListLacksIt()
    {
        Add(Test("2026-11-20", lastSeen: FetchUtc, student: false));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc.AddMinutes(3)); // uczeń pobrał listę później i jej nie ma

        Assert.False((await DetectAsync()).Any);
    }

    [Fact]
    public async Task ElementSeenByOneAccountInLatestFetchIsNotGone()
    {
        // Rodzic już go nie widzi, uczeń (pobranie później) nadal tak.
        Add(Test("2026-11-20", lastSeen: FetchUtc.AddMinutes(3)));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc.AddMinutes(3));

        Assert.False((await DetectAsync()).Any);
    }

    [Fact]
    public async Task PastElementsAreIgnoredAndMarkedWithoutNotifications()
    {
        var past = Add(Test("2026-11-10"));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        Assert.False((await DetectAsync()).Any);

        Assert.Empty(Notifications());
        Assert.NotNull(Reload(past).GoneHandledUtc); // nie jest wczytywany w kolejnych cyklach
    }

    [Fact]
    public async Task ElementDatedTodayIsReported()
    {
        Add(Test("2026-11-12"));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        Assert.Equal(2, (await DetectAsync()).Removed);
    }

    [Fact]
    public async Task ModuleWithoutFetchesIsNeverReported()
    {
        Add(Test("2026-11-20"));
        FetchAll(ModuleKind.Grades, FetchUtc, FetchUtc);

        Assert.False((await DetectAsync()).Any);
    }

    [Fact]
    public async Task MovedTestReplacesPendingNewNotificationsAndGoneElementGetsNone()
    {
        var old = Add(Test("2026-11-17"), (RecipientGroup.Parents, NotificationStatus.Sent), (RecipientGroup.Student, NotificationStatus.Sent));
        var replacement = Add(Test("2026-11-20", lesson: "5", lastSeen: FetchUtc), (RecipientGroup.Parents, NotificationStatus.Pending), (RecipientGroup.Student, NotificationStatus.Pending));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.Removed);
        var notifications = Notifications();
        Assert.All(notifications.Where(n => n.ElementId == replacement.Id), n =>
        {
            Assert.Equal(NotificationKind.Moved, n.Kind);
            Assert.Equal(old.Id, n.RelatedElementId);
            Assert.Equal(NotificationStatus.Pending, n.Status);
        });
        Assert.DoesNotContain(notifications, n => n.ElementId == old.Id && n.Kind == NotificationKind.Removed);
        Assert.NotNull(Reload(old).GoneHandledUtc);
    }

    [Fact]
    public async Task ChangedDescriptionIsNotPairedSoYouGetNewAndRemoved()
    {
        Add(Test("2026-11-17", "Test 2"), (RecipientGroup.Parents, NotificationStatus.Sent));
        var other = Add(Test("2026-11-20", "Test 2 – poprawiony zakres", lastSeen: FetchUtc), (RecipientGroup.Parents, NotificationStatus.Pending));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(0, result.Moved);
        Assert.Equal(2, result.Removed);
        Assert.Equal(NotificationKind.New, Notifications().Single(n => n.ElementId == other.Id).Kind);
    }

    [Fact]
    public async Task AmbiguousPairIsNotMerged()
    {
        Add(Test("2026-11-17"), (RecipientGroup.Parents, NotificationStatus.Sent));
        Add(Test("2026-11-18"), (RecipientGroup.Parents, NotificationStatus.Sent));
        var replacement = Add(Test("2026-11-24", lastSeen: FetchUtc), (RecipientGroup.Parents, NotificationStatus.Pending));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(0, result.Moved);
        Assert.Equal(4, result.Removed);
        Assert.Equal(NotificationKind.New, Notifications().Single(n => n.ElementId == replacement.Id).Kind);
    }

    [Fact]
    public async Task AlreadyAnnouncedReplacementOnlyGivesHintInRemovalNotification()
    {
        var old = Add(Test("2026-11-17"), (RecipientGroup.Parents, NotificationStatus.Sent));
        var replacement = Add(Test("2026-11-20", lesson: "5", lastSeen: FetchUtc), (RecipientGroup.Parents, NotificationStatus.Sent));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync(studentNotifications: false);

        Assert.Equal(1, result.Removed);
        var removal = Notifications().Single(n => n.Kind == NotificationKind.Removed);
        Assert.Equal(old.Id, removal.ElementId);
        Assert.Equal(replacement.Id, removal.RelatedElementId);
    }

    [Fact]
    public async Task ElementGoneBeforeAnyoneWasToldIsCancelledSilently()
    {
        var gone = Add(Test("2026-11-20"), (RecipientGroup.Parents, NotificationStatus.Pending), (RecipientGroup.Student, NotificationStatus.Pending));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(2, result.Cancelled);
        Assert.Equal(0, result.Removed);
        Assert.All(Notifications().Where(n => n.ElementId == gone.Id), n =>
        {
            Assert.Equal(NotificationStatus.Skipped, n.Status);
            Assert.Equal(NotificationKind.New, n.Kind);
        });
    }

    [Fact]
    public async Task StudentGetsNoRemovalWhenStudentNotificationsAreDisabledOrElementIsParentOnly()
    {
        Add(Test("2026-11-20"));
        Add(Test("2026-11-21", parent: true, student: false));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        await DetectAsync(studentNotifications: false);
        Assert.All(Notifications(), n => Assert.Equal(RecipientGroup.Parents, n.Group));
        Assert.Equal(2, Notifications().Count);
    }

    [Fact]
    public async Task SuspiciouslyManyDisappearancesAreSuppressedAndLogged()
    {
        for (var i = 0; i < ChangeDetector.MaxChangesPerCycle + 1; i++) Add(Test($"2026-12-{i + 1:00}", description: $"Opis {i}"));
        FetchAll(ModuleKind.Schedule, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(ChangeDetector.MaxChangesPerCycle + 1, result.Suppressed);
        Assert.Empty(Notifications());
        Assert.Contains(_log.Messages(LogLevel.Warning), m => m.Contains("zniknęło naraz 21"));
        Assert.False((await DetectAsync()).Any); // obsłużone, nie wraca w kolejnych cyklach
    }

    [Fact]
    public async Task HomeworkIsOnlyJudgedWhenTheSevenDayListWouldStillContainIt()
    {
        ElementEntity Homework(string assigned, string due, string topic) => DigestElements.Make(ElementTypes.Homework,
            [new Field("Przedmiot", "Matematyka"), new Field("Temat", topic), new Field("Nauczyciel", "Anna Nowak"),
                new Field("Data udostępnienia", assigned), new Field("Termin wykonania", due)], lastSeen: SeenUtc);
        var old = Add(Homework("2026-10-20", "2026-11-20", "Dawno zadane"));
        var recent = Add(Homework("2026-11-10", "2026-11-20", "Świeżo zadane"));
        FetchAll(ModuleKind.Homework, FetchUtc, FetchUtc);

        var result = await DetectAsync();

        Assert.Equal(2, result.Removed);
        Assert.All(Notifications(), n => Assert.Equal(recent.Id, n.ElementId));
        Assert.Null(Reload(old).GoneHandledUtc);
    }

    [Fact]
    public async Task HomeworkDeadlineChangeIsPairedWhenOnlyDeadlineDiffers()
    {
        ElementEntity Homework(string due, DateTime seen) => DigestElements.Make(ElementTypes.Homework,
            [new Field("Przedmiot", "Matematyka"), new Field("Temat", "Zadania 1-5"), new Field("Nauczyciel", "Anna Nowak"),
                new Field("Data udostępnienia", "2026-11-10"), new Field("Termin wykonania", due)], lastSeen: seen);
        var old = Add(Homework("2026-11-16", SeenUtc), (RecipientGroup.Parents, NotificationStatus.Sent));
        var replacement = Add(Homework("2026-11-18", FetchUtc), (RecipientGroup.Parents, NotificationStatus.Pending));
        FetchAll(ModuleKind.Homework, FetchUtc, FetchUtc);

        var result = await DetectAsync(studentNotifications: false);

        Assert.Equal(1, result.Moved);
        var moved = Notifications().Single(n => n.ElementId == replacement.Id);
        Assert.Equal(NotificationKind.Moved, moved.Kind);
        Assert.Equal(old.Id, moved.RelatedElementId);
    }

    [Fact]
    public async Task TimetableChangesAreRemovedButNeverPaired()
    {
        var cancelled = Add(DigestElements.Timetable("2026-11-13", "zastępstwo", "1", "Język polski", lastSeen: SeenUtc), (RecipientGroup.Parents, NotificationStatus.Sent));
        var other = Add(DigestElements.Timetable("2026-11-16", "zastępstwo", "1", "Język polski", lastSeen: FetchUtc), (RecipientGroup.Parents, NotificationStatus.Pending));
        FetchAll(ModuleKind.Timetable, FetchUtc, FetchUtc);

        var result = await DetectAsync(studentNotifications: false);

        Assert.Equal(1, result.Removed);
        Assert.Equal(cancelled.Id, Notifications().Single(n => n.Kind == NotificationKind.Removed).ElementId);
        Assert.Equal(NotificationKind.New, Notifications().Single(n => n.ElementId == other.Id).Kind);
    }

    [Fact]
    public void PairKeyIgnoresDateLessonAndTeacherButNotSubjectTitleOrDescription()
    {
        string? Key(ElementEntity e) => TrackedElements.PairKey(e.Type, TrackedElements.Fields(e));

        Assert.Equal(Key(Test("2026-11-17")), Key(Test("2026-11-20", lesson: "5")));
        Assert.NotEqual(Key(Test("2026-11-17")), Key(Test("2026-11-17", description: "Inny opis")));
        Assert.NotEqual(Key(Test("2026-11-17")), Key(Test("2026-11-17", subject: "Fizyka")));
        // Wpisy na cały dzień i zastępstwa/odwołania z numerem lekcji w treści nie są łączone.
        Assert.Null(Key(DigestElements.Schedule("2026-12-24", "Boże Narodzenie: SP 33", "Boże Narodzenie: SP 33")));
        Assert.Null(Key(DigestElements.Schedule("2026-11-03", "Izabela Śmieszniak na lekcji nr: 2 (Matematyka)", "Odwołane zajęcia", "2")));
    }
}

public sealed class ChangeDetectionCycleTests
{
    private static readonly ObservedItem OldTest = ScheduleItems.Test(17, "Język niemiecki", "Test 2 - Das ist meine Familie", 4);
    private static readonly ObservedItem MovedTest = ScheduleItems.Test(20, "Język niemiecki", "Test 2 - Das ist meine Familie", 5);

    private static async Task<CycleHarness> StartAsync(params ObservedItem[] items)
    {
        var h = new CycleHarness();
        ScheduleItems.Set(h.Parent, items);
        ScheduleItems.Set(h.Student, items);
        await h.BaselineAsync();
        h.Clock.Advance(TimeSpan.FromHours(1));
        return h;
    }

    [Fact]
    public async Task MovedTestProducesOneChangeMailInsteadOfNewEntryMail()
    {
        using var h = await StartAsync(OldTest);
        ScheduleItems.Set(h.Parent, MovedTest);
        ScheduleItems.Set(h.Student, MovedTest);

        await h.PingAsync(fullReview: true);

        var parent = Assert.Single(h.ParentMails);
        Assert.Equal("[Librus] Zmiana terminu: Sprawdzian - Język niemiecki, z 2026-11-17 (wtorek, za 5 dni) na 2026-11-20 (piątek, za 8 dni)", parent.Subject);
        Assert.Contains("Zmiana terminu: z 2026-11-17 (wtorek, za 5 dni), lekcja 4 na 2026-11-20 (piątek, za 8 dni), lekcja 5", parent.Body);
        Assert.Contains("Zmiana terminu w terminarzu", parent.HtmlBody);
        Assert.Single(h.StudentMails);
        Assert.DoesNotContain(h.Mail.Sent, m => m.Subject.StartsWith("[Librus] Terminarz:", StringComparison.Ordinal));
        Assert.Equal(1, h.Stats.Snapshot().MovedElements / 2); // rodzice i uczeń

        // Kolejny ping niczego już nie wysyła.
        h.Mail.Sent.Clear();
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.PingAsync(fullReview: true);
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task RemovedTestProducesRemovalMailOnceForParentsAndStudent()
    {
        using var h = await StartAsync(OldTest);
        ScheduleItems.Set(h.Parent);
        ScheduleItems.Set(h.Student);

        await h.PingAsync(fullReview: true);

        Assert.Equal("[Librus] Usunięto z terminarza: Sprawdzian - Język niemiecki, 2026-11-17 (wtorek, za 5 dni)", Assert.Single(h.ParentMails).Subject);
        Assert.Contains("Usunięto z terminarza", Assert.Single(h.StudentMails).HtmlBody);
        Assert.Contains("Status: usunięty z Librusa", h.ParentMails[0].Body);

        h.Mail.Sent.Clear();
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.PingAsync(fullReview: true);
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task ElementThatReturnsAndVanishesAgainDoesNotRepeatTheRemovalMail()
    {
        using var h = await StartAsync(OldTest);
        ScheduleItems.Set(h.Parent);
        ScheduleItems.Set(h.Student);
        await h.PingAsync(fullReview: true);
        Assert.Single(h.ParentMails);

        h.Mail.Sent.Clear();
        ScheduleItems.Set(h.Parent, OldTest);
        ScheduleItems.Set(h.Student, OldTest);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.PingAsync(fullReview: true);
        ScheduleItems.Set(h.Parent);
        ScheduleItems.Set(h.Student);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.PingAsync(fullReview: true);

        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task ElementOnlyOnParentListIsNeverReportedAsRemoved()
    {
        using var h = new CycleHarness();
        ScheduleItems.Set(h.Parent, OldTest);
        await h.BaselineAsync();

        for (var i = 0; i < 3; i++)
        {
            h.Clock.Advance(TimeSpan.FromHours(1));
            await h.PingAsync(fullReview: true);
        }

        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task RemovalWaitsForStudentAccountThatCouldNotLogIn()
    {
        using var h = await StartAsync(OldTest);
        ScheduleItems.Set(h.Parent);
        ScheduleItems.Set(h.Student);
        h.Student.LoginError = new HttpRequestException("brak sieci");

        await h.PingAsync(fullReview: true);
        Assert.Empty(h.ParentMails);

        h.Student.LoginError = null;
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.PingAsync(fullReview: true);
        Assert.Single(h.ParentMails);
        Assert.Single(h.StudentMails);
    }

    [Fact]
    public async Task RemovalOfPastEntryIsNotReported()
    {
        using var h = await StartAsync(ScheduleItems.Test(10, "Fizyka", "Dawny sprawdzian", 2));
        ScheduleItems.Set(h.Parent);
        ScheduleItems.Set(h.Student);

        await h.PingAsync(fullReview: true);

        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task ChangeNotificationsAreRetriedLikeAnyOtherAfterSendFailure()
    {
        using var h = await StartAsync(OldTest);
        ScheduleItems.Set(h.Parent);
        ScheduleItems.Set(h.Student);
        var fail = true;
        h.Mail.Failure = mail => fail && mail.Subject.StartsWith("[Librus] Usunięto", StringComparison.Ordinal) ? new IOException("SMTP") : null;

        await h.PingAsync(fullReview: true);
        Assert.Empty(h.ParentMails);

        fail = false;
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.PingAsync(fullReview: true);
        Assert.Single(h.ParentMails);
    }
}

public sealed class ChangeMailTemplateTests
{
    private static readonly FakeClock Clock = new(new DateTime(2026, 11, 12, 10, 0, 0));

    private static ElementEntity Test(string date, string lesson = "4") =>
        DigestElements.Schedule(date, "Sprawdzian", "Język niemiecki", lesson, "Selwesiuk Jolanta", "Test 2");

    private static OutgoingMail Mail(ElementEntity element, NotificationKind kind, ElementEntity? related = null) =>
        MailTemplates.ForElement(element, ["rodzic@example.com"], Clock, null, kind, related, new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void MovedMailNamesBothDatesAndKeepsElementFields()
    {
        var mail = Mail(Test("2026-11-20", "5"), NotificationKind.Moved, Test("2026-11-17"));

        Assert.Equal("[Librus] Zmiana terminu: Sprawdzian - Język niemiecki, z 2026-11-17 (wtorek, za 5 dni) na 2026-11-20 (piątek, za 8 dni)", mail.Subject);
        Assert.StartsWith("Zmiana terminu w terminarzu", mail.Body);
        Assert.Contains("Zmiana terminu: z 2026-11-17 (wtorek, za 5 dni), lekcja 4 na 2026-11-20 (piątek, za 8 dni), lekcja 5", mail.Body);
        Assert.Contains("Opis: Test 2", mail.Body);
        Assert.Contains("Wykryto: 2026-11-12 10:00", mail.Body);
        Assert.Contains("<h1 style=\"margin:0 0 12px;font-size:18px;font-weight:600;\">Zmiana terminu w terminarzu</h1>", mail.HtmlBody);
    }

    [Fact]
    public void RemovedMailSaysItWasRemovedAndMentionsPossibleMove()
    {
        var plain = Mail(Test("2026-11-17"), NotificationKind.Removed);
        Assert.Equal("[Librus] Usunięto z terminarza: Sprawdzian - Język niemiecki, 2026-11-17 (wtorek, za 5 dni)", plain.Subject);
        Assert.StartsWith("Usunięto z terminarza", plain.Body);
        Assert.Contains("Status: usunięty z Librusa (zniknął z listy)", plain.Body);
        Assert.DoesNotContain("Możliwe przeniesienie", plain.Body);

        var hinted = Mail(Test("2026-11-17"), NotificationKind.Removed, Test("2026-11-20", "5"));
        Assert.Equal("[Librus] Usunięto z terminarza: Sprawdzian - Język niemiecki, 2026-11-17 (wtorek, za 5 dni) - możliwe przeniesienie na 2026-11-20 (piątek, za 8 dni)", hinted.Subject);
        Assert.Contains("Możliwe przeniesienie: na 2026-11-20 (piątek, za 8 dni), lekcja 5", hinted.Body);
    }

    [Fact]
    public void RemovedMailsCoverTimetableChangesAndHomework()
    {
        var substitution = Mail(DigestElements.Timetable("2026-11-13", "zastępstwo", "1", "Język polski"), NotificationKind.Removed);
        Assert.Equal("[Librus] Usunięto zmianę w planie lekcji: 2026-11-13 (piątek, jutro), lekcja 1 - zastępstwo", substitution.Subject);
        Assert.StartsWith("Usunięto zmianę w planie lekcji", substitution.Body);

        var homework = Mail(DigestElements.Homework("2026-11-16", "Matematyka", "Zadania 1-5"), NotificationKind.Removed);
        Assert.Equal("[Librus] Usunięto zadanie domowe: Matematyka - Zadania 1-5, termin 2026-11-16 (poniedziałek, za 4 dni)", homework.Subject);

        var allDay = Mail(DigestElements.Schedule("2026-11-27", "Dzień wolny: SP 33", "Dzień wolny: SP 33"), NotificationKind.Removed);
        Assert.Equal("[Librus] Usunięto z terminarza: Dzień wolny: SP 33, 2026-11-27 (piątek, za 15 dni)", allDay.Subject);
    }

    [Fact]
    public void MovedHomeworkMailNamesTheDeadlines()
    {
        var mail = Mail(DigestElements.Homework("2026-11-18", "Matematyka", "Zadania 1-5"), NotificationKind.Moved,
            DigestElements.Homework("2026-11-16", "Matematyka", "Zadania 1-5"));
        Assert.Equal("[Librus] Zmiana terminu zadania domowego: Matematyka - Zadania 1-5, z 2026-11-16 (poniedziałek, za 4 dni) na 2026-11-18 (środa, za 6 dni)", mail.Subject);
        Assert.StartsWith("Zmiana terminu zadania domowego", mail.Body);
    }

    [Fact]
    public void MovedWithoutPreviousElementFallsBackToNewEntryMail()
    {
        var mail = Mail(Test("2026-11-20", "5"), NotificationKind.Moved);
        Assert.StartsWith("[Librus] Terminarz: Sprawdzian", mail.Subject);
    }

    [Fact]
    public void NewEntryMailIsUnchanged()
    {
        var mail = MailTemplates.ForElement(Test("2026-11-20", "5"), ["rodzic@example.com"], Clock);
        Assert.StartsWith("[Librus] Terminarz: Sprawdzian - Język niemiecki, 2026-11-20", mail.Subject);
        Assert.StartsWith("Nowy wpis w terminarzu", mail.Body);
    }
}
