using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Digest;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LibrusNotifier.Tests;

internal static class DigestElements
{
    private static readonly DateTime Seen = new(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc);

    public static ElementEntity Make(string type, IEnumerable<Field> fields, bool parent = true, bool student = true, DateTime? lastSeen = null) => new()
    {
        Type = type,
        Key = Guid.NewGuid().ToString("N"),
        DataJson = FieldJson.Serialize(fields),
        VisibleToParent = parent,
        VisibleToStudent = student,
        FirstSeenUtc = Seen,
        LastSeenUtc = lastSeen ?? Seen
    };

    private static void Add(List<Field> fields, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value)) fields.Add(new Field(label, value));
    }

    public static ElementEntity Schedule(string date, string title, string subject, string? lesson = null, string? teacher = null,
        string? description = null, string? hour = null, bool parent = true, bool student = true, DateTime? lastSeen = null)
    {
        var fields = new List<Field>();
        Add(fields, "Data", date);
        Add(fields, "Godzina", hour);
        Add(fields, "Nr lekcji", lesson);
        Add(fields, "Przedmiot", subject);
        Add(fields, "Tytuł", title);
        Add(fields, "Nauczyciel", teacher);
        Add(fields, "Opis", description);
        return Make(ElementTypes.ScheduleEvent, fields, parent, student, lastSeen);
    }

    public static ElementEntity Timetable(string date, string change, string lesson, string subject, string hours = "08:00–08:45",
        string teacherRoom = "Anna Nowak", string? teacherSwap = null, bool parent = true, bool student = true, DateTime? lastSeen = null)
    {
        var fields = new List<Field>();
        Add(fields, "Zmiana", change);
        Add(fields, "Data", date);
        Add(fields, "Nr lekcji", lesson);
        Add(fields, "Godziny", hours);
        Add(fields, "Przedmiot", subject);
        Add(fields, "Nauczyciel i sala", teacherRoom);
        Add(fields, "Nauczyciel (zmiana)", teacherSwap);
        return Make(ElementTypes.TimetableChange, fields, parent, student, lastSeen);
    }

    /// <summary>Ocena do zapisu w bazie: <c>Id</c> nadaje baza (<see cref="GradeElements"/> numeruje własnym licznikiem, bo oceny z testów bez bazy go potrzebują).</summary>
    public static ElementEntity Grade(string subject, string mark, string date, string semester = "1", bool student = true)
    {
        var grade = GradeElements.Create(subject, mark, date, semester: semester, student: student);
        grade.Id = 0;
        return grade;
    }

    public static ElementEntity Homework(string due, string subject, string topic, bool parent = true, bool student = true) =>
        Make(ElementTypes.Homework,
            [new Field("Przedmiot", subject), new Field("Temat", topic), new Field("Nauczyciel", "Anna Nowak"),
                new Field("Data udostępnienia", "2026-11-01"), new Field("Termin wykonania", due)], parent, student);
}

public sealed class DigestEventBuilderTests
{
    private static readonly SchoolCalendar Calendar = new([]);

    private static IReadOnlyList<DigestEvent> Build(RecipientGroup group = RecipientGroup.Parents, params ElementEntity[] elements) =>
        DigestEventBuilder.Build(elements, [], group, Calendar);

    private static DigestEvent Single(ElementEntity element) => Assert.Single(Build(RecipientGroup.Parents, element));

    [Fact]
    public void TestsQuizzesAndHomeworkAreRecognised()
    {
        var test = Single(DigestElements.Schedule("2026-11-12", "Sprawdzian", "Język angielski", "2", "Gubernat Julia", "Unit 2"));
        Assert.Equal(EventCategory.Test, test.Category);
        Assert.Equal("Sprawdzian", test.Label);
        Assert.Equal(2, test.Lesson);
        Assert.Equal("Język angielski", test.Subject);
        Assert.Equal("Unit 2", test.Detail);
        Assert.Equal("Gubernat Julia", test.Note);

        Assert.Equal(EventCategory.Test, Single(DigestElements.Schedule("2026-11-12", "Praca klasowa", "Historia", "3", "X")).Category);
        Assert.Equal("Praca klasowa", Single(DigestElements.Schedule("2026-11-12", "Praca klasowa", "Historia", "3", "X")).Label);

        var quiz = Single(DigestElements.Schedule("2026-11-12", "Kartkówka", "Fizyka", "4", "Y"));
        Assert.Equal(EventCategory.Quiz, quiz.Category);
        Assert.Equal("Kartkówka", quiz.Label);

        var homework = Single(DigestElements.Schedule("2026-11-13", "zadanie domowe", "Język polski", "3", "Michalik Aleksandra", "przeczytać lekturę"));
        Assert.Equal(EventCategory.Homework, homework.Category);
        Assert.Equal("Zadanie domowe", homework.Label);
    }

    [Fact]
    public void SubstitutionsAndCancellationsFromScheduleAreParsed()
    {
        const string text = "Zastępstwo z Karina Mirowska na lekcji nr: 1 (Język polski)";
        var substitution = Single(DigestElements.Schedule("2026-11-13", text, text, "1"));
        Assert.Equal(EventCategory.Substitution, substitution.Category);
        Assert.Equal(1, substitution.Lesson);
        Assert.Equal("Język polski", substitution.Subject);
        Assert.Equal("Zastępstwo: Karina Mirowska", substitution.Detail);

        var cancelled = Single(DigestElements.Schedule("2026-11-03", "Izabela Śmieszniak na lekcji nr: 2 (Matematyka)", "Odwołane zajęcia", "2"));
        Assert.Equal(EventCategory.Cancelled, cancelled.Category);
        Assert.Equal(2, cancelled.Lesson);
        Assert.Equal("Matematyka", cancelled.Subject);
        Assert.Equal("Izabela Śmieszniak", cancelled.Note);
    }

    [Fact]
    public void SingleLineSchoolEntryIsDayOffAndOtherEntriesFallBackToEventOrOther()
    {
        var dayOff = Single(DigestElements.Schedule("2026-11-11", "Święto Niepodległości: SP 33", "Święto Niepodległości: SP 33"));
        Assert.Equal(EventCategory.DayOff, dayOff.Category);
        Assert.Equal("Dzień wolny", dayOff.Label);
        Assert.Equal("Święto Niepodległości: SP 33", dayOff.Subject);
        Assert.Null(dayOff.Lesson);

        Assert.Equal("Wszystkich Świętych: SP 33", Single(DigestElements.Schedule("2026-11-01", "Wszystkich  Świętych: SP 33", "Wszystkich  Świętych: SP 33")).Subject);

        var trip = Single(DigestElements.Schedule("2026-11-18", "Wycieczka klasowa", "Wychowawca", teacher: "Jan Kowalski"));
        Assert.Equal(EventCategory.Event, trip.Category);
        Assert.Equal("Wycieczka klasowa", trip.Label);

        var other = Single(DigestElements.Schedule("2026-11-18", "Konsultacje", "Matematyka", teacher: "Jan Kowalski"));
        Assert.Equal(EventCategory.Other, other.Category);
        Assert.Equal("Konsultacje", other.Label);
    }

    [Fact]
    public void AllDaySchoolEntryOnSchoolDayIsSchoolEventNotDayOff()
    {
        // Środa 1.09.2027 jest dniem nauki, a niedziela 1.11.2026 i święto 11.11.2026 nie.
        var start = Single(DigestElements.Schedule("2027-09-01", "Rozpoczęcie roku szkolnego: SP 33", "Rozpoczęcie roku szkolnego: SP 33"));
        Assert.Equal(EventCategory.Event, start.Category);
        Assert.Equal("Wydarzenie szkolne", start.Label);
        Assert.Equal("Rozpoczęcie roku szkolnego: SP 33", start.Subject);

        Assert.Equal(EventCategory.DayOff, Single(DigestElements.Schedule("2026-11-01", "Wszystkich Świętych: SP 33", "Wszystkich Świętych: SP 33")).Category);

        // Dzień wolny ustalony przez szkołę w dniu nauki jest rozpoznany po nazwie.
        var directors = Single(DigestElements.Schedule("2026-11-27", "Dzień wolny od zajęć dydaktycznych: SP 33", "Dzień wolny od zajęć dydaktycznych: SP 33"));
        Assert.Equal(EventCategory.DayOff, directors.Category);
    }

    [Fact]
    public void TimetableChangesAreClassified()
    {
        var cancelled = Single(DigestElements.Timetable("2026-11-09", "odwołane", "3", "Przyroda", "09:50–10:35", "Piela Joanna"));
        Assert.Equal(EventCategory.Cancelled, cancelled.Category);
        Assert.Equal("09:50–10:35", cancelled.Hours);
        Assert.Equal("Piela Joanna", cancelled.Note);

        var substitution = Single(DigestElements.Timetable("2026-11-13", "zastępstwo", "1", "Język polski", teacherSwap: "Michalik Aleksandra -> Mirowska Karina"));
        Assert.Equal(EventCategory.Substitution, substitution.Category);
        Assert.Equal("Zastępstwo: Michalik Aleksandra → Mirowska Karina", substitution.Detail);

        var room = Single(DigestElements.Make(ElementTypes.TimetableChange,
            [new Field("Zmiana", "zastępstwo"), new Field("Data", "2026-11-13"), new Field("Nr lekcji", "1"), new Field("Przedmiot", "Język polski"),
                new Field("Nauczyciel (zmiana)", "A B -> C D"), new Field("Sala (zmiana)", "64 -> [brak]")]));
        Assert.Equal("Zastępstwo: A B → C D\nSala: 64 → brak", room.Detail);

        var other = Single(DigestElements.Timetable("2026-11-13", "zamiana lekcji", "2", "Muzyka"));
        Assert.Equal(EventCategory.Other, other.Category);
        Assert.Equal("Zamiana lekcji", other.Label);
    }

    [Fact]
    public void SubstitutionPresentInScheduleAndTimetableIsReportedOnce()
    {
        const string text = "Zastępstwo z Karina Mirowska na lekcji nr: 1 (Język polski)";
        var events = Build(RecipientGroup.Parents,
            DigestElements.Schedule("2026-11-13", text, text, "1"),
            DigestElements.Timetable("2026-11-13", "zastępstwo", "1", "Język polski", teacherSwap: "Michalik Aleksandra -> Mirowska Karina"));

        var merged = Assert.Single(events);
        Assert.Equal(EventSource.Timetable, merged.Source);
        Assert.Equal("08:00–08:45", merged.Hours);
        Assert.Equal("Zastępstwo: Michalik Aleksandra → Mirowska Karina", merged.Detail);
    }

    [Fact]
    public void WholeDayOffFromTimetableCollapsesToOneEventNamedByScheduleEntry()
    {
        var elements = Enumerable.Range(1, 7)
            .Select(i => DigestElements.Timetable("2026-11-11", "dzień wolny szkoły", i.ToString(), "Przyroda"))
            .Append(DigestElements.Schedule("2026-11-11", "Święto Niepodległości: SP 33", "Święto Niepodległości: SP 33"))
            .ToArray();

        var dayOff = Assert.Single(Build(RecipientGroup.Parents, elements));
        Assert.Equal("Święto Niepodległości: SP 33", dayOff.Subject);

        var onlyTimetable = Assert.Single(Build(RecipientGroup.Parents, elements.Take(7).ToArray()));
        Assert.Equal(EventCategory.DayOff, onlyTimetable.Category);
        Assert.Equal("Dzień wolny od zajęć", onlyTimetable.Subject);
    }

    [Fact]
    public void HomeworkIsDatedByDeadlineAndMergedWithMatchingScheduleEntry()
    {
        var homework = Single(DigestElements.Homework("2026-11-16", "Matematyka", "Zadania 1-5 str. 40"));
        Assert.Equal(new DateOnly(2026, 11, 16), homework.Date);
        Assert.Equal("Zadanie domowe", homework.Label);
        Assert.Equal("Zadania 1-5 str. 40", homework.Detail);

        var merged = Assert.Single(Build(RecipientGroup.Parents,
            DigestElements.Homework("2026-11-16", "Matematyka", "Zadania 1-5 str. 40"),
            DigestElements.Schedule("2026-11-16", "zadanie domowe", "Matematyka", "2", "Anna Nowak", "Oddać w zeszycie")));
        Assert.Equal("Zadania 1-5 str. 40\nOddać w zeszycie", merged.Detail);

        Assert.Equal(2, Build(RecipientGroup.Parents,
            DigestElements.Homework("2026-11-16", "Matematyka", "Zadania"),
            DigestElements.Schedule("2026-11-17", "zadanie domowe", "Matematyka", "2", "Anna Nowak")).Count);
    }

    [Fact]
    public void StudentSeesOnlyElementsVisibleOnStudentAccount()
    {
        var elements = new[]
        {
            DigestElements.Schedule("2026-11-12", "Sprawdzian", "Fizyka", "1", "X", student: false),
            DigestElements.Schedule("2026-11-12", "Kartkówka", "Chemia", "2", "Y")
        };
        Assert.Equal(2, Build(RecipientGroup.Parents, elements).Count);
        Assert.Equal("Chemia", Assert.Single(Build(RecipientGroup.Student, elements)).Subject);
    }

    [Fact]
    public void ElementThatDisappearedFromLatestFetchIsDropped()
    {
        var fetch = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var stale = DigestElements.Schedule("2026-11-19", "Sprawdzian", "Fizyka", "1", "X", lastSeen: fetch.AddHours(-1));
        var fresh = DigestElements.Schedule("2026-11-20", "Sprawdzian", "Fizyka", "1", "X", lastSeen: fetch);
        var baselines = new[]
        {
            new BaselineEntity { Account = AccountKind.Parent, Module = ModuleKind.Schedule, LastFetchUtc = fetch },
            new BaselineEntity { Account = AccountKind.Student, Module = ModuleKind.Schedule, LastFetchUtc = fetch.AddMinutes(-3) }
        };

        var events = DigestEventBuilder.Build([stale, fresh], baselines, RecipientGroup.Parents, new SchoolCalendar([]));

        Assert.Equal(new DateOnly(2026, 11, 20), Assert.Single(events).Date);
    }

    [Fact]
    public void ElementSeenOnlyByStudentIsNotDroppedByNewerParentFetch()
    {
        var studentFetch = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var element = DigestElements.Schedule("2026-11-19", "Sprawdzian", "Fizyka", "1", "X", parent: false, lastSeen: studentFetch);
        var baselines = new[]
        {
            new BaselineEntity { Account = AccountKind.Parent, Module = ModuleKind.Schedule, LastFetchUtc = studentFetch.AddMinutes(5) },
            new BaselineEntity { Account = AccountKind.Student, Module = ModuleKind.Schedule, LastFetchUtc = studentFetch }
        };

        Assert.Single(DigestEventBuilder.Build([element], baselines, RecipientGroup.Student, new SchoolCalendar([])));
    }

    [Fact]
    public void HomeworkIsNotDroppedByFetchTimestamps()
    {
        var homework = DigestElements.Homework("2026-11-20", "Matematyka", "Zadania");
        var baselines = new[] { new BaselineEntity { Account = AccountKind.Parent, Module = ModuleKind.Homework, LastFetchUtc = homework.LastSeenUtc.AddDays(3) } };
        Assert.Single(DigestEventBuilder.Build([homework], baselines, RecipientGroup.Parents, new SchoolCalendar([])));
    }

    [Fact]
    public void EventsAreSortedByDayThenAllDayFirstThenByLesson()
    {
        var events = Build(RecipientGroup.Parents,
            DigestElements.Schedule("2026-11-14", "Sprawdzian", "Fizyka", "5", "X"),
            DigestElements.Schedule("2026-11-13", "Kartkówka", "Chemia", "2", "X"),
            DigestElements.Schedule("2026-11-13", "Sprawdzian", "Biologia", "1", "X"),
            DigestElements.Schedule("2026-11-13", "Dzień Sportu", "Dzień Sportu", teacher: "Y"),
            DigestElements.Schedule("2026-11-13", "Wycieczka", "Wychowawca", hour: "08:30", teacher: "Y"));

        Assert.Equal(["Dzień Sportu", "Wychowawca", "Biologia", "Chemia", "Fizyka"], events.Select(e => e.Subject));
    }

    [Fact]
    public void ElementsWithoutParsableDateAreSkipped()
    {
        Assert.Empty(Build(RecipientGroup.Parents, DigestElements.Schedule("brak", "Sprawdzian", "Fizyka", "1", "X")));
    }
}

public sealed class DigestBuilderTests
{
    private static readonly SchoolCalendar Calendar = new([new SchoolHolidayPeriod(new DateOnly(2027, 1, 18), new DateOnly(2027, 1, 31), "Ferie zimowe")]);

    private static DigestEvent Event(string date, EventCategory category = EventCategory.Test, string subject = "Fizyka") =>
        new(DateOnly.ParseExact(date, "yyyy-MM-dd"), category, "Sprawdzian", 1, "", subject, "", "", EventSource.Schedule);

    private static DailyDigest Build(DigestKind kind, DateTime now, params DigestEvent[] events) =>
        DigestBuilder.Build(kind, events, new FakeClock(now), Calendar);

    private static readonly DigestEvent[] Week =
    [
        Event("2026-11-12", subject: "Czwartek"),
        Event("2026-11-13", subject: "Piątek"),
        Event("2026-11-14", EventCategory.Event, "Sobota"),
        Event("2026-11-16", subject: "Poniedziałek"),
        Event("2026-11-20", subject: "Piątek za tydzień")
    ];

    private static IEnumerable<string> Subjects(DigestSection section) => section.Days.SelectMany(d => d.Events).Select(e => e.Subject);

    [Fact]
    public void MorningStartsWithTodayAndListsRestChronologically()
    {
        var digest = Build(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0), Week);

        Assert.Equal("Dzisiaj", digest.PrimarySection!.Title);
        Assert.Equal(["Czwartek"], Subjects(digest.PrimarySection));
        Assert.Equal(["Piątek", "Sobota", "Poniedziałek", "Piątek za tydzień"], Subjects(digest.Upcoming));
        Assert.Equal(new DateOnly(2026, 11, 12), digest.PrimaryDate);
        Assert.Null(digest.Banner);
        Assert.Equal(["Dzisiaj", "Kolejne wydarzenia"], digest.Sections.Select(s => s.Title));
    }

    [Fact]
    public void AfternoonStartsWithTomorrowWhenItIsSchoolDay()
    {
        var digest = Build(DigestKind.Afternoon, new DateTime(2026, 11, 12, 15, 0, 0), Week);

        Assert.Equal("Jutro", digest.PrimarySection!.Title);
        Assert.Equal(["Piątek"], Subjects(digest.PrimarySection));
        Assert.Equal(["Sobota", "Poniedziałek", "Piątek za tydzień"], Subjects(digest.Upcoming));
        Assert.Null(digest.Banner);
    }

    [Theory]
    [InlineData(2026, 11, 15)] // niedziela: jutro poniedziałek jest szkolny
    public void AfternoonBeforeSchoolDayAfterWeekendUsesTomorrow(int year, int month, int day)
    {
        var digest = Build(DigestKind.Afternoon, new DateTime(year, month, day, 15, 0, 0), Week);
        Assert.Equal("Jutro", digest.PrimarySection!.Title);
        Assert.Equal(["Poniedziałek"], Subjects(digest.PrimarySection));
    }

    [Theory]
    [InlineData(13)] // piątek: jutro sobota
    [InlineData(14)] // sobota: jutro niedziela
    public void AfternoonBeforeWeekendHighlightsMondayAndPutsItBeforeTheRest(int day)
    {
        var digest = Build(DigestKind.Afternoon, new DateTime(2026, 11, day, 15, 0, 0), Week);

        var primary = digest.PrimarySection!;
        Assert.Equal("Poniedziałek", primary.Title);
        Assert.Equal(new DateOnly(2026, 11, 16), digest.PrimaryDate);
        Assert.Equal(["Poniedziałek"], Subjects(primary));
        Assert.Contains("pierwszy dzień nauki po weekendzie", primary.Subtitle);
        Assert.Contains("weekend", digest.Banner);
        Assert.Same(primary, digest.Sections[0]);
        // Poniedziałku nie ma już na liście kolejnych; wydarzenie z soboty jest tam tylko, gdy sobota jeszcze nie minęła.
        var upcoming = Subjects(digest.Upcoming).ToList();
        Assert.DoesNotContain("Poniedziałek", upcoming);
        Assert.Equal(day == 13, upcoming.Contains("Sobota"));
        Assert.Contains("Piątek za tydzień", upcoming);
    }

    [Fact]
    public void AfternoonBeforePublicHolidayHighlightsFirstSchoolDayWithDate()
    {
        // Wtorek 10.11: środa 11.11 to święto, czwartek 12.11 jest pierwszym dniem nauki.
        var digest = Build(DigestKind.Afternoon, new DateTime(2026, 11, 10, 15, 0, 0), Week);

        Assert.Equal("Czwartek, 12.11.2026", digest.PrimarySection!.Title);
        Assert.Equal(["Czwartek"], Subjects(digest.PrimarySection));
        Assert.Contains("pierwszy dzień nauki po wolnym", digest.PrimarySection.Subtitle);
        Assert.Contains("święto", digest.Banner);
    }

    [Fact]
    public void AfternoonBeforeBreakNamesTheBreakAndFirstSchoolDay()
    {
        // Niedziela 17.01.2027: ferie 18–31.01, pierwszy dzień nauki to poniedziałek 1.02.
        var digest = Build(DigestKind.Afternoon, new DateTime(2027, 1, 17, 15, 0, 0), Event("2027-02-01", subject: "Po feriach"), Event("2027-02-02"));

        Assert.Equal("Poniedziałek, 1.02.2027", digest.PrimarySection!.Title);
        Assert.Equal(["Po feriach"], Subjects(digest.PrimarySection));
        Assert.Contains("Ferie zimowe", digest.Banner);
    }

    [Fact]
    public void EmptyPrimarySectionStillExistsAndHasEmptyText()
    {
        var digest = Build(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0), Event("2026-11-20"));
        Assert.Equal(0, digest.PrimarySection!.EventCount);
        Assert.NotEmpty(digest.PrimarySection.EmptyText);
        Assert.True(digest.HasFutureEvents);
    }

    [Fact]
    public void FutureEventsIgnoreTodayAndThePast()
    {
        var digest = Build(DigestKind.Afternoon, new DateTime(2026, 11, 12, 15, 0, 0), Event("2026-11-12"), Event("2026-11-10"));
        Assert.False(digest.HasFutureEvents);
        Assert.Equal(0, digest.Upcoming.EventCount);
        Assert.Equal(0, digest.PrimarySection!.EventCount);
    }
}

public sealed class DigestMailTemplateTests
{
    private static readonly SchoolCalendar Calendar = new([]);

    private static DigestEvent Event(string date, EventCategory category, string label, string subject, int? lesson = 2, string detail = "", string note = "",
        string hours = "") => new(DateOnly.ParseExact(date, "yyyy-MM-dd"), category, label, lesson, hours, subject, detail, note, EventSource.Schedule);

    private static OutgoingMail Mail(DigestKind kind, DateTime now, params DigestEvent[] events)
    {
        var clock = new FakeClock(now);
        return DigestMailTemplates.Build(DigestBuilder.Build(kind, events, clock, Calendar), ["rodzic@example.com"], clock, Calendar);
    }

    [Fact]
    public void EveryCategoryHasItsOwnPastelColor()
    {
        var colors = Enum.GetValues<EventCategory>().Select(DigestPalette.Background).ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Equal("#f28b82", DigestPalette.Background(EventCategory.Test));
        Assert.Equal("#fad0cc", DigestPalette.Background(EventCategory.Quiz));
        Assert.Equal("#d9c4f2", DigestPalette.Background(EventCategory.Homework));
        Assert.Equal("#b9e4c4", DigestPalette.Background(EventCategory.Substitution));
    }

    [Fact]
    public void HtmlColorsTypeFieldsAndOnlyPrimarySectionIsAChecklist()
    {
        var mail = Mail(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0),
            Event("2026-11-12", EventCategory.Test, "Sprawdzian", "Język angielski", 2, "Unit 2", "Gubernat Julia"),
            Event("2026-11-12", EventCategory.Homework, "Zadanie domowe", "Matematyka", null),
            Event("2026-11-13", EventCategory.Substitution, "Zastępstwo", "Język polski", 1, "Zastępstwo: Mirowska Karina", "", "08:00–08:45"),
            Event("2026-11-16", EventCategory.Quiz, "Kartkówka", "Fizyka", 4));

        var html = mail.HtmlBody!;
        Assert.Contains("background:#f28b82;color:#1f1f1f", html);
        Assert.Contains("background:#d9c4f2;color:#1f1f1f", html);
        Assert.Contains("background:#b9e4c4;color:#1f1f1f", html);
        Assert.Contains("background:#fad0cc;color:#1f1f1f", html);
        // Pola do odhaczenia tylko przy dwóch wydarzeniach z sekcji „Dzisiaj”.
        Assert.Equal(2, html.Split("&#9744;").Length - 1);
        Assert.Contains("Dzisiaj", html);
        Assert.Contains("Kolejne wydarzenia", html);
        Assert.Contains("Piątek, 13.11.2026 (jutro)", html);
        Assert.Contains("Poniedziałek, 16.11.2026 (za 4 dni)", html);
        Assert.Contains("Kolory:", html);
        Assert.True(html.IndexOf("Dzisiaj", StringComparison.Ordinal) < html.IndexOf("Kolejne wydarzenia", StringComparison.Ordinal));
    }

    [Fact]
    public void ValuesAreHtmlEncoded()
    {
        var mail = Mail(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0),
            Event("2026-11-12", EventCategory.Other, "<b>Inne</b>", "<script>alert(1)</script>", 1, "a & b <i>", "<u>x</u>"));

        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.DoesNotContain("<b>Inne</b>", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", mail.HtmlBody);
        Assert.Contains("a &amp; b &lt;i&gt;", mail.HtmlBody);
    }

    [Fact]
    public void TodaysAndTomorrowsDatesInDetailsAreEmphasized()
    {
        var mail = Mail(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0),
            Event("2026-11-12", EventCategory.Test, "Sprawdzian", "Fizyka", 1, "Poprawa 2026-11-13"));
        Assert.Contains("<strong>2026-11-13 (piątek, jutro)</strong>", mail.HtmlBody);
    }

    [Fact]
    public void MorningSubjectAndTextListTodayAsChecklist()
    {
        var mail = Mail(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0),
            Event("2026-11-12", EventCategory.Test, "Sprawdzian", "Język angielski", 2),
            Event("2026-11-12", EventCategory.Substitution, "Zastępstwo", "Język polski", 1),
            Event("2026-11-13", EventCategory.Quiz, "Kartkówka", "Fizyka", 4));

        Assert.Equal("[Librus] Zdarzenia na dziś, czwartek 2026-11-12: 2 (sprawdzian, zastępstwo); kolejne: 1", mail.Subject);
        Assert.Contains("[ ] Sprawdzian · lekcja 2 · Język angielski", mail.Body);
        Assert.Contains("  - Kartkówka · lekcja 4 · Fizyka", mail.Body);
        Assert.Contains("Piątek, 13.11.2026 (jutro)", mail.Body);
        Assert.True(mail.Body.IndexOf("DZISIAJ", StringComparison.Ordinal) < mail.Body.IndexOf("KOLEJNE WYDARZENIA", StringComparison.Ordinal));
    }

    [Fact]
    public void AfternoonSubjectsNameTheDay()
    {
        Assert.Equal("[Librus] Zdarzenia na jutro, piątek 2026-11-13: brak; kolejne: 1",
            Mail(DigestKind.Afternoon, new DateTime(2026, 11, 12, 15, 0, 0), Event("2026-11-16", EventCategory.Test, "Sprawdzian", "Fizyka")).Subject);
        Assert.Equal("[Librus] Zdarzenia na poniedziałek 2026-11-16: 1 (sprawdzian); kolejne: 0",
            Mail(DigestKind.Afternoon, new DateTime(2026, 11, 13, 15, 0, 0), Event("2026-11-16", EventCategory.Test, "Sprawdzian", "Fizyka")).Subject);
    }

    [Fact]
    public void SubjectUsesAccusativeDayNameAndKindsInsteadOfCustomLabels()
    {
        var summer = new SchoolCalendar([new SchoolHolidayPeriod(new DateOnly(2027, 6, 26), new DateOnly(2027, 8, 31), "Wakacje letnie")]);
        var clock = new FakeClock(new DateTime(2027, 8, 27, 15, 0, 0));
        var digest = DigestBuilder.Build(DigestKind.Afternoon, [], clock, summer);
        Assert.Equal("[Librus] Zdarzenia na środę 2027-09-01: brak; kolejne: 0", DigestMailTemplates.BuildSubject(digest));

        var morning = Mail(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0),
            Event("2026-11-12", EventCategory.Event, "Dzień Sportu", "Dzień Sportu", null),
            Event("2026-11-12", EventCategory.Quiz, "Kartkówka", "Fizyka"),
            Event("2026-11-12", EventCategory.Test, "Praca klasowa", "Historia"),
            Event("2026-11-12", EventCategory.Other, "Konsultacje", "Matematyka"));
        Assert.Equal("[Librus] Zdarzenia na dziś, czwartek 2026-11-12: 4 (sprawdzian, kartkówka, wydarzenie…); kolejne: 0", morning.Subject);
    }

    [Fact]
    public void BannerIsShownWhenTomorrowIsFree()
    {
        var mail = Mail(DigestKind.Afternoon, new DateTime(2026, 11, 13, 15, 0, 0), Event("2026-11-16", EventCategory.Test, "Sprawdzian", "Fizyka"));
        Assert.Contains("nie ma nauki: weekend", mail.HtmlBody);
        Assert.Contains("nie ma nauki: weekend", mail.Body);
        Assert.Contains("pierwszy dzień nauki po weekendzie", mail.HtmlBody);
    }

    [Fact]
    public void EmptyDigestStillExplainsThereIsNothing()
    {
        var mail = Mail(DigestKind.Morning, new DateTime(2026, 11, 12, 6, 30, 0));
        Assert.Contains("Na dziś nie ma zdarzeń w terminarzu.", mail.HtmlBody);
        Assert.Contains("Brak kolejnych wydarzeń w terminarzu.", mail.Body);
        Assert.Equal("[Librus] Zdarzenia na dziś, czwartek 2026-11-12: brak; kolejne: 0", mail.Subject);
    }
}

public sealed class DigestServiceTests : IDisposable
{
    private static readonly SchoolCalendar Calendar = new([new SchoolHolidayPeriod(new DateOnly(2027, 1, 18), new DateOnly(2027, 1, 31), "Ferie zimowe")]);
    private const string Parent = CycleHarness.ParentAddress;
    private const string Student = CycleHarness.StudentAddress;

    private readonly TestDatabase _db = new();
    private readonly FakeMailSender _mail = new();
    private readonly ListLog _log = new();

    public void Dispose() => _db.Dispose();

    private (DigestService Service, FakeClock Clock, AdminReporter Admin, DailyStats Stats) Create(DateTime now, bool studentRecipients = true)
    {
        var clock = new FakeClock(now);
        var stats = new DailyStats(clock.Today, clock.Now);
        var admin = new AdminReporter(_mail, [CycleHarness.AdminAddress], _log, stats);
        var service = new DigestService(_mail, [Parent], studentRecipients ? [Student] : [], _db.Create, Calendar, clock, _log, admin, stats);
        return (service, clock, admin, stats);
    }

    private void Seed(params ElementEntity[] elements)
    {
        using var db = _db.Create();
        db.Elements.AddRange(elements);
        db.SaveChanges();
    }

    private List<OutgoingMail> Digests(string address) => _mail.To(address).Where(m => m.Subject.StartsWith("[Librus] Zdarzenia na")
        || m.Subject.StartsWith("[Librus] Nadchodzące")).ToList();

    private static ElementEntity UpcomingTest(string date = "2026-11-19") => DigestElements.Schedule(date, "Sprawdzian", "Fizyka", "2", "X");

    [Fact]
    public async Task MorningDigestIsSentAtSixThirtyToParentsAndStudentOnceOnly()
    {
        Seed(DigestElements.Schedule("2026-11-12", "Sprawdzian", "Język angielski", "2", "X"), UpcomingTest());
        var (service, clock, _, stats) = Create(new DateTime(2026, 11, 12, 6, 29, 0));

        await service.RunDueAsync(CancellationToken.None);
        Assert.Empty(_mail.Sent);

        clock.SetLocal(new DateTime(2026, 11, 12, 6, 30, 0));
        await service.RunDueAsync(CancellationToken.None);
        clock.SetLocal(new DateTime(2026, 11, 12, 6, 31, 0));
        await service.RunDueAsync(CancellationToken.None);

        var parent = Assert.Single(Digests(Parent));
        var student = Assert.Single(Digests(Student));
        Assert.StartsWith("[Librus] Zdarzenia na dziś, czwartek 2026-11-12: 1 (sprawdzian); kolejne: 1", parent.Subject);
        Assert.Equal(parent.Subject, student.Subject);
        Assert.Equal(1, stats.Snapshot().DigestsSent[RecipientGroup.Parents]);
        Assert.Equal(1, stats.Snapshot().DigestsSent[RecipientGroup.Student]);
        using var db = _db.Create();
        Assert.Equal(2, await db.DigestLogs.CountAsync());
    }

    [Fact]
    public async Task StudentDigestContainsOnlyEventsVisibleToStudent()
    {
        Seed(DigestElements.Schedule("2026-11-12", "Sprawdzian", "Fizyka", "1", "X", student: false),
            DigestElements.Schedule("2026-11-12", "Kartkówka", "Chemia", "2", "X"));
        var (service, _, _, _) = Create(new DateTime(2026, 11, 12, 6, 30, 0));

        await service.RunDueAsync(CancellationToken.None);

        Assert.Contains("Fizyka", Digests(Parent).Single().Body);
        Assert.DoesNotContain("Fizyka", Digests(Student).Single().Body);
        Assert.Contains("Chemia", Digests(Student).Single().Body);
    }

    [Fact]
    public async Task EveryDigestEndsWithTheGradeTableOfTheCurrentSemester()
    {
        Seed(UpcomingTest(),
            DigestElements.Grade("Matematyka", "5", "2026-10-10"), DigestElements.Grade("Matematyka", "4+", "2026-11-03"),
            DigestElements.Grade("Język polski", "3", "2026-10-20"),
            DigestElements.Grade("Fizyka", "1", "2025-10-10"), // poprzedni rok szkolny
            DigestElements.Grade("Chemia", "2", "2027-02-10", semester: "2")); // drugi semestr
        var (service, clock, _, _) = Create(new DateTime(2026, 11, 12, 6, 30, 0));

        await service.RunDueAsync(CancellationToken.None);
        clock.SetLocal(new DateTime(2026, 11, 12, 15, 0, 0));
        await service.RunDueAsync(CancellationToken.None);

        var mails = Digests(Parent);
        Assert.Equal(2, mails.Count); // poranne i popołudniowe
        foreach (var mail in mails)
        {
            Assert.Contains("Oceny z bieżącego semestru", mail.HtmlBody);
            Assert.Contains("Język polski: 3\nMatematyka: 5, 4+", mail.Body.Replace("\r\n", "\n"));
            Assert.DoesNotContain("Fizyka: 1", mail.Body);
            Assert.DoesNotContain("Chemia: 2", mail.Body);
        }
    }

    [Fact]
    public async Task StudentGradeTableHasOnlyGradesOfTheStudentAccount()
    {
        Seed(UpcomingTest(),
            DigestElements.Grade("Matematyka", "5", "2026-10-10"),
            DigestElements.Grade("Fizyka", "2", "2026-10-11", student: false));
        var (service, _, _, _) = Create(new DateTime(2026, 11, 12, 6, 30, 0));

        await service.RunDueAsync(CancellationToken.None);

        Assert.Contains("Fizyka: 2", Digests(Parent).Single().Body);
        var student = Digests(Student).Single().Body;
        Assert.Contains("Matematyka: 5", student);
        Assert.DoesNotContain("Fizyka: 2", student);
    }

    [Fact]
    public async Task GradeTableSaysThereAreNoGradesAndSummerHasNoTable()
    {
        Seed(UpcomingTest("2027-09-02"));
        var (service, clock, _, _) = Create(new DateTime(2026, 11, 12, 6, 30, 0));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Contains("Brak ocen w bieżącym semestrze.", Digests(Parent).Single().Body);

        clock.SetLocal(new DateTime(2027, 7, 15, 15, 0, 0)); // lipiec: nie ma bieżącego semestru (kalendarz testu nie zna wakacji, więc zestawienie wychodzi)
        await service.RunDueAsync(CancellationToken.None);
        var summer = Digests(Parent).Last();
        Assert.StartsWith("[Librus] Zdarzenia na", summer.Subject);
        Assert.DoesNotContain("Oceny z bieżącego semestru", summer.HtmlBody);
        Assert.DoesNotContain("Brak ocen", summer.Body);
    }

    [Fact]
    public async Task NoStudentMailWhenStudentHasNoRecipients()
    {
        Seed(UpcomingTest());
        var (service, _, _, _) = Create(new DateTime(2026, 11, 12, 15, 0, 0), studentRecipients: false);

        await service.RunDueAsync(CancellationToken.None);

        Assert.Single(_mail.Sent);
        Assert.Empty(_mail.To(Student));
    }

    [Fact]
    public async Task MorningDigestIsNotSentOnFreeDays()
    {
        Seed(UpcomingTest("2026-11-16"));
        var (service, clock, _, _) = Create(new DateTime(2026, 11, 14, 6, 30, 0)); // sobota

        await service.RunDueAsync(CancellationToken.None);
        Assert.Empty(_mail.Sent);

        clock.SetLocal(new DateTime(2026, 11, 14, 15, 0, 0));
        await service.RunDueAsync(CancellationToken.None);
        Assert.StartsWith("[Librus] Zdarzenia na poniedziałek 2026-11-16", Assert.Single(Digests(Parent)).Subject);
    }

    [Fact]
    public async Task MorningDigestIsNotSentOnPublicHolidayAndDuringFerie()
    {
        Seed(UpcomingTest("2027-02-01"));
        var (service, clock, _, _) = Create(new DateTime(2026, 11, 11, 6, 30, 0)); // święto
        await service.RunDueAsync(CancellationToken.None);
        clock.SetLocal(new DateTime(2027, 1, 20, 6, 30, 0)); // ferie
        await service.RunDueAsync(CancellationToken.None);

        Assert.DoesNotContain(_mail.Sent, m => m.Subject.Contains("na dziś"));
    }

    [Fact]
    public async Task AfternoonDigestIsSentEvenWithoutEventsDuringSchoolYear()
    {
        var (service, _, _, _) = Create(new DateTime(2026, 11, 12, 15, 0, 0));

        await service.RunDueAsync(CancellationToken.None);

        var mail = Assert.Single(Digests(Parent));
        Assert.Equal("[Librus] Zdarzenia na jutro, piątek 2026-11-13: brak; kolejne: 0", mail.Subject);
    }

    [Fact]
    public async Task AfternoonDigestInHolidaysNeedsFutureEvents()
    {
        var (service, clock, _, _) = Create(new DateTime(2027, 1, 20, 15, 0, 0));

        await service.RunDueAsync(CancellationToken.None);
        Assert.Empty(_mail.Sent);

        // Nowy dzień ferii, tym razem z przyszłym wydarzeniem po feriach.
        Seed(UpcomingTest("2027-02-01"));
        clock.SetLocal(new DateTime(2027, 1, 21, 15, 0, 0));
        await service.RunDueAsync(CancellationToken.None);

        var mail = Assert.Single(Digests(Parent));
        Assert.Contains("2027-02-01", mail.Subject);
    }

    [Fact]
    public async Task EventsOfTodayDoNotCountAsFutureForAfternoonDigestInHolidays()
    {
        Seed(DigestElements.Schedule("2027-01-20", "Wycieczka", "Wychowawca", teacher: "X"));
        var (service, _, _, _) = Create(new DateTime(2027, 1, 20, 15, 0, 0));

        await service.RunDueAsync(CancellationToken.None);

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task RestartDoesNotSendAlreadySentDigestAgain()
    {
        Seed(UpcomingTest());
        var now = new DateTime(2026, 11, 12, 6, 30, 0);
        await Create(now).Service.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, _mail.Sent.Count);

        // Nowy proces (świeża pamięć usługi) po restarcie o 9:00 – poranne było już wysłane.
        var (restarted, clock, _, _) = Create(now.AddHours(2));
        await restarted.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, _mail.Sent.Count);

        // Popołudniowe z restartu o 15:10 jest jeszcze należne i wychodzi raz.
        clock.SetLocal(new DateTime(2026, 11, 12, 15, 10, 0));
        await restarted.RunDueAsync(CancellationToken.None);
        await restarted.RunDueAsync(CancellationToken.None);
        Assert.Equal(4, _mail.Sent.Count);
    }

    [Fact]
    public async Task DigestMissedByMoreThanMaxLatenessIsSkipped()
    {
        Seed(UpcomingTest());
        var (service, clock, _, _) = Create(new DateTime(2026, 11, 12, 8, 31, 0)); // poranne było na 6:30, minęły ponad 2 h

        await service.RunDueAsync(CancellationToken.None);

        Assert.Empty(_mail.Sent);
        Assert.Contains(_log.Messages(LogLevel.Info), m => m.Contains("pominięte") && m.Contains("poranne"));

        clock.SetLocal(new DateTime(2026, 11, 12, 15, 0, 0));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, _mail.Sent.Count);
    }

    [Fact]
    public async Task DigestJustInsideMaxLatenessIsStillSent()
    {
        Seed(UpcomingTest());
        var (service, _, _, _) = Create(new DateTime(2026, 11, 12, 8, 29, 0));

        await service.RunDueAsync(CancellationToken.None);

        Assert.Equal(2, _mail.Sent.Count);
    }

    [Fact]
    public async Task FailedSendIsReportedAndRetriedAfterDelayWithoutDuplicates()
    {
        Seed(UpcomingTest());
        var (service, clock, _, stats) = Create(new DateTime(2026, 11, 12, 6, 30, 0));
        var fail = true;
        _mail.Failure = mail => fail && mail.To.Contains(Parent) ? new IOException("SMTP niedostępny") : null;

        await service.RunDueAsync(CancellationToken.None);

        Assert.Empty(Digests(Parent));
        Assert.Contains(_log.Messages(LogLevel.Error), m => m.Contains("Nieudana wysyłka zestawienia") && m.Contains("SMTP niedostępny"));
        Assert.Single(_mail.To(CycleHarness.AdminAddress));
        Assert.Equal(1, stats.Snapshot().MailFailures);
        var attempts = _mail.Attempts;

        clock.Advance(TimeSpan.FromMinutes(2));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Equal(attempts, _mail.Attempts); // pauza po błędzie

        fail = false;
        clock.Advance(TimeSpan.FromMinutes(4));
        await service.RunDueAsync(CancellationToken.None);
        await service.RunDueAsync(CancellationToken.None);
        Assert.Single(Digests(Parent));
        Assert.Single(Digests(Student)); // uczeń dostał swoje od razu, bez duplikatu po ponowieniu rodziców
    }

    [Fact]
    public async Task PingLoopSendsDigestAtSixThirtyWhileWaitingForPings()
    {
        using var h = new CycleHarness(start: new DateTime(2026, 11, 12, 6, 0, 0));
        using (var db = h.Db.Create())
        {
            db.Elements.Add(DigestElements.Schedule("2026-11-12", "Sprawdzian", "Fizyka", "2", "X"));
            db.SaveChanges();
        }
        var stats = new DailyStats(h.Clock.Today, h.Clock.Now);
        var digest = new DigestService(h.Mail, [CycleHarness.ParentAddress], [CycleHarness.StudentAddress], h.Db.Create, Calendar, h.Clock, h.Log, h.Admin, stats);
        using var guard = new DayGuard(h.Clock, h.Clock.Today, CancellationToken.None);
        // Okno 6:00–8:00 co 50 min: pingi o 6:00 i 6:50 (dwa pierwsze kroki), potem pętla się kończy; zestawienie ma wyjść pomiędzy.
        var windows = new[] { new PollingWindow("test", TimeSpan.FromHours(6), TimeSpan.FromHours(8), TimeSpan.FromMinutes(50), TimeSpan.Zero, JitterMode.Symmetric) };
        var digestTimes = new List<DateTime>();
        h.Mail.Failure = mail =>
        {
            if (mail.Subject.StartsWith("[Librus] Zdarzenia na dziś", StringComparison.Ordinal)) digestTimes.Add(h.Clock.Now);
            return null;
        };
        var loop = new PingLoop(new PollingScheduler(h.Clock.Today, windows, new FixedRandom(0.5)), h.Cycle, h.Clock, guard, h.Log, h.Admin,
            (delay, _) =>
            {
                h.Clock.Advance(delay);
                return Task.CompletedTask;
            }, digest);

        await loop.RunAsync();

        Assert.Equal(2, digestTimes.Count); // rodzice i uczeń, raz
        Assert.All(digestTimes, at => Assert.Equal(new DateTime(2026, 11, 12, 6, 30, 0), at));
    }
}

public sealed class DigestPersistenceTests
{
    [Fact]
    public async Task EveryFetchRefreshesBaselineTimestampTogetherWithLastSeen()
    {
        using var h = new CycleHarness();
        h.Parent.Lists[ModuleKind.Schedule].Add(LibrusLists.FromSchedule(2026, 11, new Dictionary<int, List<LibrusApixNet.Event>>
        {
            [19] = [new LibrusApixNet.Event("Sprawdzian", "Fizyka", new Dictionary<string, string>(), "19", 2, "unknown", "/terminarz/sprawdzian/1")]
        })[0]);
        await h.BaselineAsync();

        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.PingAsync(fullReview: true);

        await using var db = h.Db.Create();
        var fetched = await db.Baselines.SingleAsync(b => b.Account == AccountKind.Parent && b.Module == ModuleKind.Schedule);
        var element = await db.Elements.SingleAsync(e => e.Type == ElementTypes.ScheduleEvent);
        Assert.Equal(h.Clock.UtcNow.UtcDateTime, fetched.LastFetchUtc);
        Assert.Equal(fetched.LastFetchUtc, element.LastSeenUtc);
        Assert.True(fetched.LastFetchUtc > fetched.CompletedUtc);
    }

    [Fact]
    public async Task MigrationFillsLastFetchFromFirstFetchForExistingDatabases()
    {
        var path = Path.Combine(Path.GetTempPath(), $"librus-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = NotifierDbContext.Create(path))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20261111144747_Initial");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Baselines (Account, Module, CompletedUtc) VALUES ('Parent', 'Schedule', '2026-11-11 20:54:05')");
            }
            await using (var db = NotifierDbContext.Create(path))
            {
                await db.Database.MigrateAsync();
                var baseline = await db.Baselines.SingleAsync();
                Assert.Equal(baseline.CompletedUtc, baseline.LastFetchUtc);
                Assert.Equal(new DateTime(2026, 11, 11, 20, 54, 5, DateTimeKind.Utc), baseline.LastFetchUtc);
                Assert.Empty(await db.DigestLogs.ToListAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
