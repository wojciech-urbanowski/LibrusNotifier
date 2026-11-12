using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Tests;

public sealed class GradePaletteTests
{
    [Theory]
    [InlineData("6", "#57cf7f")]
    [InlineData("5", "#8fe0a1")]
    [InlineData("4", "#cdeccf")]
    [InlineData("3", "#fff0a8")]
    [InlineData("2", "#ffc78a")]
    [InlineData("1", "#f28b82")]
    [InlineData("+", "#8fdcc9")]
    [InlineData("-", "#f9a986")]
    public void EachMarkHasItsOwnColor(string mark, string expected) => Assert.Equal(expected, GradePalette.Background(mark));

    [Fact]
    public void ColorsAreDistinctAndEveryClassIsColored()
    {
        var colors = GradePalette.Classes.Select(c => GradePalette.Background(c)).ToList();
        Assert.All(colors, color => Assert.NotNull(color));
        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Theory]
    [InlineData("4+", "4")]
    [InlineData("3-", "3")]
    [InlineData("6-", "6")]
    [InlineData("1+", "1")]
    [InlineData(" 5 ", "5")]
    [InlineData("3\u2212", "3")] // minus matematyczny
    [InlineData("\u2212", "-")]
    [InlineData("\u2013", "-")]
    public void PlusAndMinusAfterDigitKeepsTheDigitColor(string mark, string sameAs) =>
        Assert.Equal(GradePalette.Background(sameAs), GradePalette.Background(mark));

    [Theory]
    [InlineData("nb")]
    [InlineData("np")]
    [InlineData("bz")]
    [InlineData("zw")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("5++")]
    [InlineData("4/5")]
    [InlineData("celujący")]
    public void MarksOutsideTheScaleAreGray(string mark)
    {
        Assert.Equal("#e2e2e2", GradePalette.Background(mark));
        Assert.Equal("background:#e2e2e2;color:#1f1f1f;font-weight:600;", GradePalette.CellStyle(mark));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void EmptyMarkHasNoColor(string? mark)
    {
        Assert.Null(GradePalette.Background(mark));
        Assert.Equal("", GradePalette.CellStyle(mark));
        Assert.Equal("", GradePalette.CellStyle(mark, descriptive: true));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("celujący")]
    [InlineData("bardzo dobry")]
    public void DescriptiveGradeIsAlwaysGray(string mark) =>
        Assert.Equal("background:#e2e2e2;color:#1f1f1f;font-weight:600;", GradePalette.CellStyle(mark, descriptive: true));

    [Fact]
    public void GrayIsDistinctFromEveryGradeColor() =>
        Assert.DoesNotContain(GradePalette.OtherBackground, GradePalette.Classes.Select(c => GradePalette.Background(c)));

    [Fact]
    public void PlusIsAGreenCloseToFiveAndMinusSitsBetweenTwoAndOne()
    {
        // „plus” jest odcieniem zieleni (G dominuje) różnym od piątki; „minus” leży między dwójką a jedynką.
        var (r5, g5, b5) = Rgb(GradePalette.Background("5")!);
        var (rp, gp, bp) = Rgb(GradePalette.Background("+")!);
        Assert.True(gp > rp && gp > bp);
        Assert.NotEqual((r5, g5, b5), (rp, gp, bp));

        var (_, g2, b2) = Rgb(GradePalette.Background("2")!);
        var (_, gm, bm) = Rgb(GradePalette.Background("-")!);
        var (_, g1, b1) = Rgb(GradePalette.Background("1")!);
        Assert.InRange(gm, g1, g2);
        Assert.InRange(bm, Math.Min(b1, b2), Math.Max(b1, b2));
    }

    [Fact]
    public void CellStyleSetsBackgroundAndDarkText() =>
        Assert.Equal("background:#8fe0a1;color:#1f1f1f;font-weight:600;", GradePalette.CellStyle("5"));

    private static (int R, int G, int B) Rgb(string hex) =>
        (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
}

public sealed class GradeMailTests
{
    private static readonly string[] Parent = ["rodzic@example.com"];
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 12, 10, 0, 0));

    [Fact]
    public void MarkCellInTheMailIsColoredAndPlainTextIsNot()
    {
        var mail = Mail(GradeElement("Matematyka", "5", "2026-11-12"));

        Assert.Matches(@">Ocena</th><td[^>]*background:#8fe0a1;color:#1f1f1f;font-weight:600;[^>]*>5</td>", mail.HtmlBody);
        // Kolorowane jest tylko pole z oceną.
        Assert.DoesNotMatch(@">(Przedmiot|Kategoria|Waga|Nauczyciel)</th><td[^>]*background:", mail.HtmlBody);
        Assert.DoesNotContain("background:#8fe0a1", mail.Body);
    }

    [Theory]
    [InlineData("6", "#57cf7f")]
    [InlineData("4+", "#cdeccf")]
    [InlineData("3-", "#fff0a8")]
    [InlineData("+", "#8fdcc9")]
    [InlineData("-", "#f9a986")]
    [InlineData("1", "#f28b82")]
    public void MarkCellUsesTheColorOfTheMark(string mark, string color) =>
        Assert.Contains($"background:{color};", Mail(GradeElement("Fizyka", mark, "2026-11-12")).HtmlBody);

    [Theory]
    [InlineData("zw")]
    [InlineData("np")]
    [InlineData("nb")]
    public void MarkOutsideTheScaleIsGray(string mark)
    {
        var mail = Mail(GradeElement("Wychowanie fizyczne", mark, "2026-11-12"));

        Assert.Matches($@">Ocena</th><td[^>]*background:#e2e2e2;color:#1f1f1f;font-weight:600;[^>]*>{mark}</td>", mail.HtmlBody);
        Assert.DoesNotContain("background:#8fe0a1", mail.HtmlBody); // żaden kolor ze skali
    }

    [Fact]
    public void DescriptiveGradeIsGrayWhateverTheMarkAndHasNoReport()
    {
        var element = new ElementEntity
        {
            Type = ElementTypes.DescriptiveGrade,
            Key = "d",
            DataJson = FieldJson.Serialize([new Field("Przedmiot", "Plastyka"), new Field("Ocena", "5"), new Field("Data", "2026-11-12")]),
            VisibleToParent = true,
            FirstSeenUtc = _clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, Parent, _clock, subjectGrades: [element]);

        Assert.Matches(@">Ocena</th><td[^>]*background:#e2e2e2;color:#1f1f1f;font-weight:600;[^>]*>5</td>", mail.HtmlBody);
        Assert.DoesNotContain("background:#8fe0a1", mail.HtmlBody); // piątka z opisowej nie jest zielona
        Assert.DoesNotContain("Oceny z przedmiotu", mail.HtmlBody);
    }

    [Fact]
    public void OtherFieldsAndOtherElementTypesAreNotColored()
    {
        var attendance = new ElementEntity
        {
            Type = ElementTypes.Attendance,
            Key = "a",
            DataJson = FieldJson.Serialize([new Field("Rodzaj", "nieobecność"), new Field("Ocena", "5")]),
            VisibleToParent = true,
            FirstSeenUtc = _clock.UtcNow.UtcDateTime
        };

        Assert.DoesNotContain("background:#", MailTemplates.ForElement(attendance, Parent, _clock).HtmlBody);
    }

    [Fact]
    public void UnrecognizedMarkInTheReportIsGrayAndEmptyMarkHasNoBackground()
    {
        var np = GradeElement("Matematyka", "np", "2026-11-10", "Nieprzygotowanie", "0", counts: false);
        var current = GradeElement("Matematyka", "5", "2026-11-12");
        var empty = GradeElement("Matematyka", "", "2026-11-01");

        var html = Mail(current, np, empty).HtmlBody!;
        var report = html[html.IndexOf("<h2", StringComparison.Ordinal)..];

        Assert.Matches(@"background:#e2e2e2;color:#1f1f1f;font-weight:600;[^>]*>np</td>", report);
        Assert.Matches(@"<td style=""[^""]*""></td>", report); // pusta ocena: komórka bez tła
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(report, "background:#e2e2e2"));
    }

    [Fact]
    public void WeightIsGrayInTheMailTableSoItIsNotMistakenForTheGrade()
    {
        var mail = Mail(GradeElement("Matematyka", "5", "2026-11-12", "Sprawdzian", "3"));
        var html = mail.HtmlBody!;
        var main = html[..html.IndexOf("<h2", StringComparison.Ordinal)];

        Assert.Matches(@"<th[^>]*color:#777777;[^>]*>Waga</th><td[^>]*color:#777777;[^>]*>3</td>", main);
        // Szare są tylko etykieta i wartość wagi, nie reszta tabeli ani sama ocena.
        Assert.DoesNotMatch(@"<th[^>]*color:#777777;[^>]*>(Przedmiot|Ocena|Kategoria|Data|Nauczyciel)</th>", main);
        Assert.DoesNotMatch(@">Ocena</th><td[^>]*color:#777777;", main);
        Assert.Matches(@">Ocena</th><td[^>]*color:#1f1f1f;[^>]*>5</td>", main);
    }

    [Fact]
    public void WeightColumnInTheReportIsGrayAndNeverBold()
    {
        var mail = Mail(GradeElement("Matematyka", "5", "2026-11-12", "Sprawdzian", "3"),
            GradeElement("Matematyka", "4", "2026-10-01", "Kartkówka", "2"));
        var html = mail.HtmlBody!;
        var report = html[html.IndexOf("<h2", StringComparison.Ordinal)..];

        Assert.Matches(@"<th scope=""col""[^>]*color:#777777;[^>]*>Waga</th>", report);
        Assert.DoesNotMatch(@"<th scope=""col""[^>]*color:#777777;[^>]*>(Data|Ocena|Kategoria)</th>", report);
        Assert.Matches(@"<td style=""[^""]*color:#777777;[^""]*"">3</td>", report);
        Assert.Matches(@"<td style=""[^""]*color:#777777;[^""]*"">2</td>", report);
        Assert.DoesNotMatch(@"font-weight:700;[^>]*>3</td>", report); // także w wierszu „(nowa)”
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(report, "<td style=\"[^\"]*color:#777777;").Count);
    }

    [Fact]
    public void MailWithoutSubjectGradesHasNoReport()
    {
        var mail = MailTemplates.ForElement(GradeElement("Matematyka", "5", "2026-11-12"), Parent, _clock);

        Assert.DoesNotContain("Oceny z przedmiotu", mail.HtmlBody);
        Assert.DoesNotContain("Oceny z przedmiotu", mail.Body);
    }

    [Fact]
    public void ReportListsSubjectGradesNewestFirstAndMarksTheNewOne()
    {
        var oldest = GradeElement("Matematyka", "3", "2026-09-20", "Kartkówka", "2");
        var middle = GradeElement("Matematyka", "4+", "2026-10-15", "Sprawdzian", "3");
        var current = GradeElement("Matematyka", "5", "2026-11-12", "Sprawdzian", "3");

        var mail = Mail(current, oldest, middle, current); // kolejność wejścia nie ma znaczenia

        var html = mail.HtmlBody!;
        var report = html[html.IndexOf("<h2", StringComparison.Ordinal)..];
        Assert.Contains("Oceny z przedmiotu Matematyka (od najnowszej)", report);
        AssertInOrder(report, ">2026-11-12 (nowa)<", ">2026-10-15<", ">2026-09-20<");
        // Kolory ocen w raporcie takie jak w treści maila: piątka, 4+ jak czwórka, trójka.
        AssertInOrder(report, "background:#8fe0a1;color:#1f1f1f;font-weight:600;\">5<", "background:#cdeccf;color:#1f1f1f;font-weight:600;\">4+<",
            "background:#fff0a8;color:#1f1f1f;font-weight:600;\">3<");
        Assert.Matches(@"<td[^>]*font-weight:700;[^>]*>2026-11-12 \(nowa\)</td>", report);
        Assert.DoesNotMatch(@"<td[^>]*font-weight:700;[^>]*>2026-10-15</td>", report);
        // Wiersze raportu leżą przed stopką „Widoczne na koncie”.
        Assert.True(html.IndexOf("Widoczne na koncie", StringComparison.Ordinal) > html.IndexOf("<h2", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportInPlainTextIsAListNewestFirst()
    {
        var mail = Mail(GradeElement("Matematyka", "5", "2026-11-12", "Sprawdzian", "3"),
            GradeElement("Matematyka", "3", "2026-09-20", "Kartkówka", "2"));

        var lines = mail.Body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.IndexOf("Oceny z przedmiotu Matematyka (od najnowszej):");
        Assert.True(start > 0);
        Assert.Matches(@"^2026-11-12\s+5\s+Sprawdzian, waga 3 \(nowa\)$", lines[start + 1]);
        Assert.Matches(@"^2026-09-20\s+3\s+Kartkówka, waga 2$", lines[start + 2]);
        Assert.True(lines.IndexOf("Widoczne na koncie: rodzica i ucznia") > start + 2);
        Assert.DoesNotContain("<h2", mail.Body); // wersja tekstowa bez znaczników
    }

    [Fact]
    public void SameDateIsOrderedByDetectionTimeNewestFirst()
    {
        var first = GradeElement("Matematyka", "4", "2026-11-12", "Kartkówka", "1", firstSeen: new DateTime(2026, 11, 12, 7, 0, 0, DateTimeKind.Utc));
        var second = GradeElement("Matematyka", "5", "2026-11-12", "Sprawdzian", "3", firstSeen: new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc));
        var third = GradeElement("Matematyka", "2", "2026-11-12", "Odpowiedź", "1", firstSeen: new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc));

        var mail = Mail(second, first, third);

        var html = mail.HtmlBody!;
        var report = html[html.IndexOf("<h2", StringComparison.Ordinal)..];
        AssertInOrder(report, ">Sprawdzian<", $">{System.Net.WebUtility.HtmlEncode("Kartkówka")}<", ">Odpowiedź<");
    }

    [Fact]
    public void GradeWithUnreadableDateGoesLast()
    {
        var undated = GradeElement("Matematyka", "2", "brak daty", "Kartkówka", "1");
        var dated = GradeElement("Matematyka", "5", "2026-11-12", "Sprawdzian", "3");

        var report = Mail(dated, undated).HtmlBody!;

        AssertInOrder(report[report.IndexOf("<h2", StringComparison.Ordinal)..], ">2026-11-12 (nowa)<", ">brak daty<");
    }

    [Fact]
    public void GradeNotCountedToAverageIsMarkedInTheReport()
    {
        var plus = GradeElement("Język angielski", "+", "2026-11-09", "Aktywność", "1", counts: false);
        var current = GradeElement("Język angielski", "5", "2026-11-12", "Sprawdzian", "3");

        var mail = Mail(current, plus);

        Assert.Contains(">1 (poza średnią)</td>", mail.HtmlBody);
        Assert.Matches(@"2026-11-09\s+\+\s+Aktywność, waga 1, poza średnią", mail.Body);
        Assert.Contains("background:#8fdcc9;", mail.HtmlBody); // plus w raporcie
    }

    [Fact]
    public void CurrentGradeIsInTheReportEvenWhenMissingFromTheList()
    {
        var current = GradeElement("Matematyka", "5", "2026-11-12");

        var mail = Mail(current, GradeElement("Matematyka", "3", "2026-09-20"));
        var onlyCurrent = MailTemplates.ForElement(current, Parent, _clock, subjectGrades: []);

        Assert.Contains("(nowa)", mail.HtmlBody);
        Assert.Contains("(nowa)", onlyCurrent.HtmlBody); // pusta lista oznacza „brak innych ocen”, a nie brak raportu
        Assert.Contains("Oceny z przedmiotu Matematyka", onlyCurrent.HtmlBody);
    }

    [Fact]
    public void ValuesInTheReportAreHtmlEncoded()
    {
        var evil = GradeElement("Matematyka", "5", "2026-11-12", "<script>alert(1)</script>", "3");

        var mail = Mail(evil);

        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", mail.HtmlBody);
    }

    [Fact]
    public void ReportIsOnlyForNewGrades()
    {
        var current = GradeElement("Matematyka", "5", "2026-11-12");

        var removed = MailTemplates.ForElement(current, Parent, _clock, kind: NotificationKind.Removed, subjectGrades: [current]);

        Assert.DoesNotContain("Oceny z przedmiotu", removed.HtmlBody);
    }

    private OutgoingMail Mail(ElementEntity current, params ElementEntity[] others) =>
        MailTemplates.ForElement(current, Parent, _clock, subjectGrades: others.Length == 0 ? [current] : others);

    private ElementEntity GradeElement(string subject, string mark, string date, string category = "Sprawdzian", string weight = "3",
        bool counts = true, DateTime? firstSeen = null) => GradeElements.Create(subject, mark, date, category, weight, counts,
        firstSeen ?? _clock.UtcNow.UtcDateTime);

    private static void AssertInOrder(string text, params string[] parts)
    {
        var position = -1;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, position + 1, StringComparison.Ordinal);
            Assert.True(found > position, $"Brak „{part}” za pozycją {position} w:\n{text}");
            position = found;
        }
    }
}

internal static class GradeElements
{
    private static int _id;

    /// <summary>Ocena jak z bazy (pola jak z <c>LibrusLists.FromGrades</c>), widoczna na obu kontach.</summary>
    public static ElementEntity Create(string subject, string mark, string date, string category = "Sprawdzian", string weight = "3",
        bool counts = true, DateTime? firstSeen = null, bool parent = true, bool student = true, DateTime? lastSeen = null,
        string type = ElementTypes.Grade)
    {
        var seen = firstSeen ?? new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        return new ElementEntity
        {
            Id = Interlocked.Increment(ref _id),
            Type = type,
            Key = Guid.NewGuid().ToString("N"),
            DataJson = FieldJson.Serialize([
                new Field("Przedmiot", subject), new Field("Ocena", mark), new Field("Kategoria", category), new Field("Data", date),
                new Field("Nauczyciel", "Anna Nowak"), new Field("Waga", weight), new Field("Liczona do średniej", counts ? "tak" : "nie"),
                new Field("Semestr", "1")
            ]),
            VisibleToParent = parent,
            VisibleToStudent = student,
            FirstSeenUtc = seen,
            LastSeenUtc = lastSeen ?? seen
        };
    }
}

public sealed class GradeHistoryTests
{
    private static readonly DateTime Seen = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Fetch = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private static BaselineEntity Fetched(AccountKind account, DateTime utc) =>
        new() { Account = account, Module = ModuleKind.Grades, CompletedUtc = Seen, LastFetchUtc = utc };

    private static IReadOnlyList<BaselineEntity> BothFetched => [Fetched(AccountKind.Parent, Fetch), Fetched(AccountKind.Student, Fetch)];

    [Fact]
    public void OnlyGradesOfTheSameSubjectAreIncludedIgnoringCaseAndSpaces()
    {
        var current = GradeElements.Create("Matematyka", "5", "2026-11-12", lastSeen: Fetch);
        var same = GradeElements.Create("matematyka ", "3", "2026-09-20", lastSeen: Fetch);
        var other = GradeElements.Create("Fizyka", "4", "2026-10-01", lastSeen: Fetch);

        var result = GradeHistory.ForSubject(current, [current, same, other], BothFetched, RecipientGroup.Parents);

        Assert.Equal([same, current], result.OrderBy(g => g == current));
        Assert.DoesNotContain(other, result);
    }

    [Fact]
    public void CurrentGradeIsIncludedOnceAndNeverDuplicated()
    {
        var current = GradeElements.Create("Matematyka", "5", "2026-11-12", lastSeen: Fetch);
        var fromDatabase = GradeElements.Create("Matematyka", "5", "2026-11-12", lastSeen: Fetch);
        fromDatabase.Key = current.Key; // ten sam element wczytany osobnym zapytaniem

        var result = GradeHistory.ForSubject(current, [fromDatabase], BothFetched, RecipientGroup.Parents);

        Assert.Same(current, Assert.Single(result));
    }

    [Fact]
    public void GradeThatDisappearedFromTheListIsSkipped()
    {
        var current = GradeElements.Create("Historia", "4", "2026-11-12", lastSeen: Fetch);
        var corrected = GradeElements.Create("Historia", "3", "2026-11-12", lastSeen: Seen); // zniknęła z listy po poprawieniu
        var present = GradeElements.Create("Historia", "5", "2026-10-01", lastSeen: Fetch);

        var result = GradeHistory.ForSubject(current, [corrected, present], BothFetched, RecipientGroup.Parents);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(corrected, result);
    }

    [Fact]
    public void CurrentGradeStaysEvenIfItAlreadyDisappearedBeforeTheMailWasSent()
    {
        var current = GradeElements.Create("Historia", "4", "2026-11-12", lastSeen: Seen);

        var result = GradeHistory.ForSubject(current, [current], BothFetched, RecipientGroup.Parents);

        Assert.Same(current, Assert.Single(result));
    }

    [Fact]
    public void GradeIsKeptWhenOneOfItsAccountsHasNotRefetchedTheListYet()
    {
        // Jak w wykrywaniu zniknięć: wniosek „zniknęła” wymaga, by każde konto z tą oceną pobrało listę po ostatnim jej zobaczeniu.
        var current = GradeElements.Create("Historia", "4", "2026-11-12", lastSeen: Fetch);
        var old = GradeElements.Create("Historia", "3", "2026-10-01", lastSeen: Seen);
        IReadOnlyList<BaselineEntity> baselines = [Fetched(AccountKind.Parent, Fetch), Fetched(AccountKind.Student, Seen)];

        var result = GradeHistory.ForSubject(current, [old], baselines, RecipientGroup.Parents);

        Assert.Contains(old, result);
    }

    [Fact]
    public void StudentSeesOnlyGradesFromTheStudentAccountParentsSeeAll()
    {
        var current = GradeElements.Create("Chemia", "4", "2026-11-12", lastSeen: Fetch);
        var parentOnly = GradeElements.Create("Chemia", "2", "2026-10-01", student: false, lastSeen: Fetch);
        var studentOnly = GradeElements.Create("Chemia", "5", "2026-10-05", parent: false, lastSeen: Fetch);

        var forStudent = GradeHistory.ForSubject(current, [parentOnly, studentOnly], BothFetched, RecipientGroup.Student);
        var forParents = GradeHistory.ForSubject(current, [parentOnly, studentOnly], BothFetched, RecipientGroup.Parents);

        Assert.DoesNotContain(parentOnly, forStudent);
        Assert.Contains(studentOnly, forStudent);
        Assert.Contains(parentOnly, forParents);
        Assert.Contains(studentOnly, forParents);
    }

    [Fact]
    public void OtherElementTypesAreIgnored()
    {
        var current = GradeElements.Create("Plastyka", "5", "2026-11-12", lastSeen: Fetch);
        var descriptive = GradeElements.Create("Plastyka", "", "2026-10-01", lastSeen: Fetch, type: ElementTypes.DescriptiveGrade);

        var result = GradeHistory.ForSubject(current, [descriptive], BothFetched, RecipientGroup.Parents);

        Assert.Same(current, Assert.Single(result));
    }

    [Fact]
    public void GradeWithoutSubjectHasNoHistory()
    {
        var current = GradeElements.Create("", "5", "2026-11-12", lastSeen: Fetch);

        Assert.Empty(GradeHistory.ForSubject(current, [current], BothFetched, RecipientGroup.Parents));
    }
}
