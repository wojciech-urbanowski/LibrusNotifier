using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Digest;
using LibrusNotifier.Librus;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Tests;

public sealed class PolishComparerTests
{
    [Fact]
    public void PolishLettersFollowTheirBaseLetters()
    {
        string[] shuffled = ["Żeglarstwo", "Zajęcia", "Ścieżka", "Technika", "Religia", "Matematyka", "Łacina", "Lekcje", "Ogrodnictwo", "Ósemka", "Ćwiczenia", "Cyfry"];

        var sorted = shuffled.Order(PolishComparer.Instance).ToList();

        Assert.Equal(["Cyfry", "Ćwiczenia", "Lekcje", "Łacina", "Matematyka", "Ogrodnictwo", "Ósemka", "Religia", "Ścieżka", "Technika", "Zajęcia", "Żeglarstwo"], sorted);
    }

    [Fact]
    public void IgnoresCaseAndPutsNonLettersBeforeLetters()
    {
        Assert.True(PolishComparer.Instance.Compare("język polski", "Język angielski") > 0);
        Assert.True(PolishComparer.Instance.Compare("Zajęcia", "zajęcia") != 0); // różnią się tylko wielkością: stała kolejność, nie remis
        Assert.Equal(-Math.Sign(PolishComparer.Instance.Compare("Zajęcia", "zajęcia")), Math.Sign(PolishComparer.Instance.Compare("zajęcia", "Zajęcia")));
        Assert.True(PolishComparer.Instance.Compare("Język", "Język polski") < 0);
        Assert.True(PolishComparer.Instance.Compare("Język 2", "Język a") < 0);
        Assert.Equal(0, PolishComparer.Instance.Compare("Fizyka", "Fizyka"));
        Assert.True(PolishComparer.Instance.Compare(null, "a") < 0);
        Assert.True(PolishComparer.Instance.Compare("a", null) > 0);
    }
}

public sealed class GradeTableTests
{
    private static readonly DateTime Fetch = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 12);

    private static BaselineEntity Fetched(AccountKind account, DateTime utc) =>
        new() { Account = account, Module = ModuleKind.Grades, CompletedUtc = Fetch, LastFetchUtc = utc };

    private static readonly IReadOnlyList<BaselineEntity> BothFetched = [Fetched(AccountKind.Parent, Fetch), Fetched(AccountKind.Student, Fetch)];

    private static ElementEntity Grade(string subject, string mark, string date, string? semester = "1", bool parent = true, bool student = true,
        DateTime? firstSeen = null, DateTime? lastSeen = null, string type = ElementTypes.Grade) =>
        GradeElements.Create(subject, mark, date, semester: semester, parent: parent, student: student, firstSeen: firstSeen, lastSeen: lastSeen ?? Fetch, type: type);

    private static GradeTable Build(IEnumerable<ElementEntity> grades, RecipientGroup group = RecipientGroup.Parents, DateOnly? today = null) =>
        GradeTable.Build(grades, BothFetched, group, today ?? Today)!;

    private static IEnumerable<string> Marks(GradeTable table, string subject) => table.Rows.Single(r => r.Subject == subject).Marks;

    [Fact]
    public void RowsAreSubjectsInPolishAlphabeticalOrder()
    {
        var table = Build([
            Grade("Zajęcia techniczne", "5", "2026-10-01"), Grade("Żeglarstwo", "4", "2026-10-02"), Grade("Matematyka", "3", "2026-10-03"),
            Grade("Łacina", "6", "2026-10-04"), Grade("Język polski", "4", "2026-10-05"), Grade("Ścieżka edukacyjna", "5", "2026-10-06")
        ]);

        Assert.Equal(["Język polski", "Łacina", "Matematyka", "Ścieżka edukacyjna", "Zajęcia techniczne", "Żeglarstwo"], table.Rows.Select(r => r.Subject));
    }

    [Fact]
    public void MarksOfASubjectAreChronologicalByGradeDateNotByEntryOrder()
    {
        var table = Build([
            Grade("Matematyka", "5", "2026-11-05"), // wpisana jako pierwsza, ale najnowsza
            Grade("Matematyka", "3", "2026-09-15"),
            Grade("Matematyka", "4+", "2026-10-20"),
            Grade("Fizyka", "2", "2026-10-01")
        ]);

        Assert.Equal(["3", "4+", "5"], Marks(table, "Matematyka"));
        Assert.Equal(["2"], Marks(table, "Fizyka"));
    }

    [Fact]
    public void SameDayMarksFollowDetectionOrderAndUnreadableDateComesFirst()
    {
        var early = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var table = Build([
            Grade("Matematyka", "2", "2026-10-10", firstSeen: late),
            Grade("Matematyka", "5", "2026-10-10", firstSeen: early),
            Grade("Matematyka", "1", "brak daty", firstSeen: late)
        ]);

        Assert.Equal(["1", "5", "2"], Marks(table, "Matematyka"));
    }

    [Fact]
    public void OnlyTheCurrentSemesterAndSchoolYearIsIncluded()
    {
        var firstSemester = Build([
            Grade("Matematyka", "5", "2026-10-10", "1"),
            Grade("Matematyka", "2", "2027-02-10", "2"), // drugi semestr tego samego roku (to zestawienie jest z pierwszego)
            Grade("Matematyka", "1", "2025-10-10", "1") // pierwszy semestr poprzedniego roku szkolnego, wciąż w bazie
        ]);
        Assert.Equal(1, firstSemester.Semester);
        Assert.Equal(["5"], Marks(firstSemester, "Matematyka"));

        var secondSemester = Build([
            Grade("Matematyka", "5", "2026-10-10", "1"),
            Grade("Matematyka", "4", "2027-02-10", "2"),
            Grade("Matematyka", "2", "2026-03-10", "2") // drugi semestr poprzedniego roku szkolnego
        ], today: new DateOnly(2027, 3, 1));
        Assert.Equal(2, secondSemester.Semester);
        Assert.Equal(2026, secondSemester.SchoolYear);
        Assert.Equal(["4"], Marks(secondSemester, "Matematyka"));
    }

    [Fact]
    public void LateEnteredFirstSemesterGradeStaysInTheFirstSemester()
    {
        // Liczy się kolumna „Semestr” z Librusa, nie data wpisu.
        var table = Build([Grade("Matematyka", "3", "2027-02-02", "1"), Grade("Matematyka", "5", "2027-02-03", "2")], today: new DateOnly(2027, 2, 10));

        Assert.Equal(["5"], Marks(table, "Matematyka"));
    }

    [Fact]
    public void GradesWithUnknownSemesterOrDateAreNotExcluded()
    {
        var table = Build([
            Grade("Matematyka", "4", "2026-10-10", semester: null),
            Grade("Matematyka", "3", "2026-10-11", semester: "3"),
            Grade("Matematyka", "5", "brak daty", "1")
        ]);

        Assert.Equal(["5", "4", "3"], Marks(table, "Matematyka"));
    }

    [Fact]
    public void GradesThatDisappearedFromTheListAreSkipped()
    {
        var gone = Grade("Matematyka", "1", "2026-10-10", lastSeen: Fetch.AddHours(-1)); // poprawiona w dzienniku: nie ma jej w ostatnim pobraniu
        var table = Build([gone, Grade("Matematyka", "4", "2026-10-11")]);

        Assert.Equal(["4"], Marks(table, "Matematyka"));
    }

    [Fact]
    public void StudentSeesOnlyGradesOfOwnAccountAndParentsSeeAll()
    {
        var grades = new[]
        {
            Grade("Matematyka", "5", "2026-10-10"),
            Grade("Fizyka", "2", "2026-10-11", student: false),
            Grade("Matematyka", "1", "2026-10-12", student: false)
        };

        var parents = Build(grades);
        Assert.Equal(["Fizyka", "Matematyka"], parents.Rows.Select(r => r.Subject));
        Assert.Equal(["5", "1"], Marks(parents, "Matematyka"));

        var student = Build(grades, RecipientGroup.Student);
        Assert.Equal(["Matematyka"], student.Rows.Select(r => r.Subject));
        Assert.Equal(["5"], Marks(student, "Matematyka"));
    }

    [Fact]
    public void DescriptiveGradesAndOtherTypesAreIgnored()
    {
        var table = Build([
            Grade("Plastyka", "celujący", "2026-10-10", type: ElementTypes.DescriptiveGrade),
            Grade("Matematyka", "5", "2026-10-10", type: ElementTypes.Message),
            Grade("Fizyka", "4", "2026-10-10")
        ]);

        Assert.Equal(["Fizyka"], table.Rows.Select(r => r.Subject));
    }

    [Fact]
    public void SubjectsDifferingInCaseAndWhitespaceShareARowAndBlankMarksAreSkipped()
    {
        var table = Build([
            Grade("Matematyka", "5", "2026-10-10"),
            Grade("  matematyka ", "4", "2026-10-11"),
            Grade("Matematyka", "  ", "2026-10-12"),
            Grade("", "3", "2026-10-13")
        ]);

        var row = Assert.Single(table.Rows);
        Assert.Equal("Matematyka", row.Subject);
        Assert.Equal(["5", "4"], row.Marks);
    }

    [Fact]
    public void MarksOutsideTheScaleAreKeptAsTheyAre()
    {
        var table = Build([Grade("Geografia", "np", "2026-10-10"), Grade("Geografia", "+", "2026-10-11"), Grade("Geografia", "3-", "2026-10-12")]);

        Assert.Equal(["np", "+", "3-"], Marks(table, "Geografia"));
    }

    [Fact]
    public void NoGradesGiveAnEmptyTableAndSummerGivesNone()
    {
        var empty = Build([]);
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.Columns);

        Assert.Null(GradeTable.Build([Grade("Matematyka", "5", "2027-06-01", "2")], BothFetched, RecipientGroup.Parents, new DateOnly(2027, 7, 1)));
        Assert.Null(GradeTable.Build([], BothFetched, RecipientGroup.Parents, new DateOnly(2027, 8, 31)));
    }

    [Fact]
    public void ColumnsAreTheLongestRowAndSchoolYearHasShortName()
    {
        var table = Build([
            Grade("Matematyka", "5", "2026-10-01"), Grade("Matematyka", "4", "2026-10-02"), Grade("Matematyka", "3", "2026-10-03"),
            Grade("Fizyka", "2", "2026-10-04")
        ]);

        Assert.Equal(3, table.Columns);
        Assert.Equal("2026/27", table.SchoolYearName);
        Assert.Equal("2099/00", new GradeTable(1, 2099, []).SchoolYearName);
    }

    [Theory]
    [InlineData(2026, 9, 1, 1, 2026)]
    [InlineData(2026, 12, 31, 1, 2026)]
    [InlineData(2027, 1, 31, 1, 2026)]
    [InlineData(2027, 2, 1, 2, 2026)]
    [InlineData(2027, 6, 30, 2, 2026)]
    public void CurrentTermFollowsTheCalendar(int year, int month, int day, int semester, int schoolYear) =>
        Assert.Equal(new GradeTerm(semester, schoolYear), GradeTerm.CurrentAt(new DateOnly(year, month, day)));

    [Theory]
    [InlineData(2027, 7, 1)]
    [InlineData(2027, 8, 31)]
    public void SummerHolidaysHaveNoCurrentTerm(int year, int month, int day) => Assert.Null(GradeTerm.CurrentAt(new DateOnly(year, month, day)));
}

public sealed class DigestGradeTableMailTests
{
    private static readonly SchoolCalendar Calendar = new([]);

    private static GradeTable Table(params (string Subject, string[] Marks)[] rows) =>
        new(1, 2026, rows.Select(r => new GradeTableRow(r.Subject, r.Marks)).ToList());

    private static OutgoingMail Mail(GradeTable? grades, DateTime? dataAsOf = null)
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 6, 30, 0));
        var events = new[]
        {
            new DigestEvent(new DateOnly(2026, 11, 12), EventCategory.Test, "Sprawdzian", 2, "", "Fizyka", "", "", EventSource.Schedule),
            new DigestEvent(new DateOnly(2026, 11, 13), EventCategory.Quiz, "Kartkówka", 3, "", "Chemia", "", "", EventSource.Schedule)
        };
        return DigestMailTemplates.Build(DigestBuilder.Build(DigestKind.Morning, events, clock, Calendar), ["rodzic@example.com"], clock, Calendar, dataAsOf, grades);
    }

    [Fact]
    public void TableIsTheLastSectionAfterEventsAndLegendAndBeforeTheFooter()
    {
        var html = Mail(Table(("Matematyka", ["5", "4+"])), new DateTime(2026, 11, 12, 6, 0, 0)).HtmlBody!;

        var legend = html.IndexOf("Kolory:", StringComparison.Ordinal);
        var upcoming = html.IndexOf("Kolejne wydarzenia", StringComparison.Ordinal);
        var grades = html.IndexOf("Oceny z bieżącego semestru", StringComparison.Ordinal);
        var footer = html.IndexOf("Stan danych z Librusa", StringComparison.Ordinal);
        Assert.True(upcoming > 0 && upcoming < legend, "legenda kolorów wydarzeń jest pod listami wydarzeń");
        Assert.True(legend < grades, "tabela ocen jest za legendą wydarzeń");
        Assert.True(grades < footer, "tabela ocen jest przed stopką");
        Assert.Contains("Semestr 1, rok szkolny 2026/27; oceny od najstarszej do najnowszej.", html);
    }

    [Fact]
    public void EachMarkIsItsOwnCellColoredLikeInTheNewGradeMail()
    {
        var html = Mail(Table(("Matematyka", ["6", "5", "+", "4", "3", "2", "-", "1", "np"]))).HtmlBody!;

        foreach (var (mark, background) in new[]
                 {
                     ("6", "#57cf7f"), ("5", "#8fe0a1"), ("+", "#8fdcc9"), ("4", "#cdeccf"), ("3", "#fff0a8"), ("2", "#ffc78a"), ("-", "#f9a986"),
                     ("1", "#f28b82"), ("np", "#e2e2e2")
                 })
            Assert.Contains($"background:{background};color:#1f1f1f;font-weight:600;\">{mark}</td>", html);
    }

    [Fact]
    public void MarkWithPlusOrMinusHasTheColorOfItsDigit()
    {
        var html = Mail(Table(("Matematyka", ["4+", "3-"]))).HtmlBody!;

        Assert.Contains("background:#cdeccf;color:#1f1f1f;font-weight:600;\">4+</td>", html);
        Assert.Contains("background:#fff0a8;color:#1f1f1f;font-weight:600;\">3-</td>", html);
    }

    [Fact]
    public void HeaderSpansTheLongestRowAndShorterRowsJustEnd()
    {
        var html = Mail(Table(("Fizyka", ["2"]), ("Matematyka", ["5", "4", "3", "2"]))).HtmlBody!;

        Assert.Contains("colspan=\"4\"", html);
        Assert.Contains("scope=\"row\"", html);
        var fizyka = html.IndexOf(">Fizyka</th>", StringComparison.Ordinal);
        var matematyka = html.IndexOf(">Matematyka</th>", StringComparison.Ordinal);
        Assert.True(fizyka > 0 && fizyka < matematyka, "wiersze w kolejności z tabeli");
        var fizykaRow = html[fizyka..matematyka];
        Assert.Equal(1, fizykaRow.Split("<td ").Length - 1); // jedna komórka z oceną, bez dopełniaczy
    }

    [Fact]
    public void ValuesAreHtmlEncoded()
    {
        var html = Mail(Table(("<b>Fizyka</b> & co", ["<i>5</i>"]))).HtmlBody!;

        Assert.DoesNotContain("<b>Fizyka</b>", html);
        Assert.DoesNotContain("<i>5</i>", html);
        Assert.Contains("&lt;b&gt;Fizyka&lt;/b&gt; &amp; co", html);
        Assert.Contains("&lt;i&gt;5&lt;/i&gt;", html);
    }

    [Fact]
    public void TextVersionListsMarksPerSubject()
    {
        var text = Mail(Table(("Język polski", ["4", "3+"]), ("Matematyka", ["5", "4+", "3-"])), new DateTime(2026, 11, 12, 6, 0, 0)).Body;

        const string heading = "OCENY Z BIEŻĄCEGO SEMESTRU";
        Assert.Contains($"{heading}\n{new string('=', heading.Length)}\n", text.Replace("\r\n", "\n"));
        Assert.Contains("Semestr 1, rok szkolny 2026/27; oceny od najstarszej do najnowszej.", text);
        Assert.Contains("Język polski: 4, 3+", text);
        Assert.Contains("Matematyka: 5, 4+, 3-", text);
        Assert.True(text.IndexOf("KOLEJNE WYDARZENIA", StringComparison.Ordinal) < text.IndexOf("OCENY Z BIEŻĄCEGO SEMESTRU", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Matematyka: 5", StringComparison.Ordinal) < text.IndexOf("Stan danych z Librusa", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyTableSaysThereAreNoGrades()
    {
        var mail = Mail(Table());

        Assert.Contains("Brak ocen w bieżącym semestrze.", mail.HtmlBody);
        Assert.Contains("Brak ocen w bieżącym semestrze.", mail.Body);
        Assert.DoesNotContain("scope=\"row\"", mail.HtmlBody);
        Assert.DoesNotContain("colspan", mail.HtmlBody);
    }

    [Fact]
    public void WithoutATableThereIsNoGradeSection()
    {
        var mail = Mail(null);

        Assert.DoesNotContain("Oceny z bieżącego semestru", mail.HtmlBody);
        Assert.DoesNotContain("OCENY Z BIEŻĄCEGO SEMESTRU", mail.Body);
    }

    [Fact]
    public void GradeTableDoesNotChangeTheSubject()
    {
        Assert.Equal(Mail(null).Subject, Mail(Table(("Matematyka", ["5"]))).Subject);
    }
}
