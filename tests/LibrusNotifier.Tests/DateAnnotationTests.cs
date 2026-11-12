using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Tests;

public sealed class DateAnnotationTests
{
    // 2026-11-12 to czwartek (dzień szkolny).
    private static readonly DateTime Thursday = new(2026, 11, 12, 10, 0, 0);

    private static DateAnnotator Annotator(DateTime now, SchoolCalendar? calendar = null) => new(new FakeClock(now), calendar);

    [Theory]
    [InlineData("2026-11-18", "2026-11-18 (środa, za 6 dni)")]
    [InlineData("2026-11-15", "2026-11-15 (niedziela, za 3 dni)")]
    [InlineData("2026-11-14", "2026-11-14 (sobota, pojutrze)")]
    [InlineData("2026-11-13", "2026-11-13 (piątek, jutro)")]
    [InlineData("2026-11-12", "2026-11-12 (czwartek, dziś)")]
    [InlineData("2026-11-11", "2026-11-11 (środa, wczoraj)")]
    [InlineData("2026-11-10", "2026-11-10 (wtorek, przedwczoraj)")]
    [InlineData("2026-11-07", "2026-11-07 (sobota, 5 dni temu)")]
    public void PlainTextGetsWeekdayAndRelativeDay(string input, string expected) =>
        Assert.Equal(expected, Annotator(Thursday).Plain(input));

    [Fact]
    public void DateWithTimeGetsNoteAfterTheTime() =>
        Assert.Equal("2026-11-13 12:30 (piątek, jutro)", Annotator(Thursday).Plain("2026-11-13 12:30"));

    [Fact]
    public void EveryDateInTextIsAnnotatedAndOtherTextIsKept() =>
        Assert.Equal("od 2026-11-12 (czwartek, dziś) do 2026-11-18 (środa, za 6 dni), sala 2026",
            Annotator(Thursday).Plain("od 2026-11-12 do 2026-11-18, sala 2026"));

    [Theory]
    [InlineData("2026-13-45")]
    [InlineData("12026-11-13")]
    [InlineData("2026-11-133")]
    [InlineData("brak daty")]
    [InlineData("")]
    public void NonDatesAreLeftAlone(string input) => Assert.Equal(input, Annotator(Thursday).Plain(input));

    [Theory]
    [InlineData("2026-11-12", true)] // dziś
    [InlineData("2026-11-13", true)] // jutro
    [InlineData("2026-11-14", false)] // pojutrze, dziś jest dzień szkolny
    [InlineData("2026-11-16", false)] // poniedziałek, dziś jest dzień szkolny
    [InlineData("2026-11-11", false)] // wczoraj
    public void OnSchoolDayOnlyTodayAndTomorrowAreBold(string input, bool bold)
    {
        var html = Annotator(Thursday).Html(input);

        Assert.Equal(bold, html.StartsWith("<strong>", StringComparison.Ordinal));
        if (bold) Assert.EndsWith("</strong>", html);
    }

    [Fact]
    public void BoldCoversDateWithTimeAndNoteButNotSurroundingText() =>
        Assert.Equal("na <strong>2026-11-13 12:00 (piątek, jutro)</strong> w sali",
            Annotator(Thursday).Html("na 2026-11-13 12:00 w sali"));

    [Fact]
    public void WhenTodayIsFreeFirstSchoolDayAfterItIsBold()
    {
        var saturday = new DateTime(2026, 11, 14, 10, 0, 0);
        var annotator = Annotator(saturday);

        Assert.StartsWith("<strong>", annotator.Html("2026-11-14")); // dziś
        Assert.StartsWith("<strong>", annotator.Html("2026-11-15")); // jutro
        Assert.StartsWith("<strong>", annotator.Html("2026-11-16")); // poniedziałek, pierwszy dzień po wolnym
        Assert.DoesNotContain("<strong>", annotator.Html("2026-11-17"));
    }

    [Fact]
    public void WhenTodayIsInWinterBreakFirstDayAfterBreakIsBold()
    {
        var calendar = new SchoolCalendar([new SchoolHolidayPeriod(new DateOnly(2027, 1, 18), new DateOnly(2027, 1, 31), "Ferie zimowe")]);
        var duringBreak = new DateTime(2027, 1, 20, 10, 0, 0);

        Assert.StartsWith("<strong>", Annotator(duringBreak, calendar).Html("2027-02-01")); // pierwszy dzień szkolny po feriach
        Assert.DoesNotContain("<strong>", Annotator(duringBreak, calendar).Html("2027-02-02"));
        Assert.DoesNotContain("<strong>", Annotator(duringBreak, calendar).Html("2027-01-29"));
        // Bez kalendarza ferie nie są znane, więc środa jest zwykłym dniem szkolnym i 2027-02-01 nie jest „po wolnym”.
        Assert.DoesNotContain("<strong>", Annotator(duringBreak).Html("2027-02-01"));
    }

    [Fact]
    public void HtmlEncodesTextAndKeepsLineBreaks() =>
        Assert.Equal("a &lt;b&gt; &amp;<br>\n<strong>2026-11-13 (piątek, jutro)</strong><br>\nc",
            Annotator(Thursday).Html("a <b> &\r\n2026-11-13\nc"));

    [Fact]
    public void MailAnnotatesDatesInSubjectBodyAndHtml()
    {
        var clock = new FakeClock(Thursday);
        var element = new ElementEntity
        {
            Type = ElementTypes.ScheduleEvent,
            Key = "k",
            DataJson = FieldJson.Serialize([new Field("Data", "2026-11-13"), new Field("Tytuł", "Sprawdzian"), new Field("Przedmiot", "Matematyka")]),
            VisibleToParent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        Assert.Equal("[Librus] Terminarz: Sprawdzian – Matematyka, 2026-11-13 (piątek, jutro)", mail.Subject);
        Assert.Contains("Data: 2026-11-13 (piątek, jutro)", mail.Body);
        Assert.Matches(@">Data</th><td[^>]*><strong>2026-11-13 \(piątek, jutro\)</strong></td>", mail.HtmlBody);
        Assert.DoesNotContain("<strong>", mail.Body);
    }

    [Fact]
    public void LongSubjectIsShortenedBeforeTheDateNotInsideIt()
    {
        var clock = new FakeClock(Thursday);
        var title = "Zastępstwo z Karina Mirowska na lekcji nr: 1 (Język polski)";
        var element = new ElementEntity
        {
            Type = ElementTypes.ScheduleEvent,
            Key = "k",
            DataJson = FieldJson.Serialize([new Field("Data", "2026-11-13"), new Field("Przedmiot", title + " " + title), new Field("Tytuł", title + " " + title)]),
            VisibleToParent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        Assert.True(mail.Subject.Length <= 160, mail.Subject);
        Assert.EndsWith("…, 2026-11-13 (piątek, jutro)", mail.Subject);
        Assert.StartsWith("[Librus] Terminarz: Zastępstwo z Karina", mail.Subject);
    }

    [Fact]
    public void SubjectWithoutRoomIsLeftAsIs() =>
        Assert.Equal("krótki 2026-11-13 (piątek, jutro)", Annotator(Thursday).PlainFitting("krótki 2026-11-13", 160));

    [Fact]
    public void MailAnnotatesDatesInDetailsToo()
    {
        var clock = new FakeClock(Thursday);
        var element = new ElementEntity
        {
            Type = ElementTypes.Homework,
            Key = "k",
            DataJson = FieldJson.Serialize([new Field("Przedmiot", "Historia")]),
            DetailsJson = FieldJson.Serialize([new Field("Termin wykonania", "2026-11-18")]),
            VisibleToParent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        Assert.Contains("Termin wykonania: 2026-11-18 (środa, za 6 dni)", mail.Body);
        Assert.Matches(@">Termin wykonania</th><td[^>]*>2026-11-18 \(środa, za 6 dni\)</td>", mail.HtmlBody);
    }

    [Fact]
    public void TechnicalDatesAreNotAnnotated()
    {
        var clock = new FakeClock(Thursday);
        var element = new ElementEntity
        {
            Type = ElementTypes.TimetableChange,
            Key = "k",
            DataJson = FieldJson.Serialize([new Field("Zmiana", "zastępstwo"), new Field("Data", "2026-11-13"), new Field("Data dodania", "2026-11-10")]),
            VisibleToParent = true,
            FirstSeenUtc = clock.UtcNow.UtcDateTime
        };

        var mail = MailTemplates.ForElement(element, ["rodzic@example.com"], clock);

        Assert.Contains("Data dodania: 2026-11-10\n", mail.Body.Replace("\r\n", "\n"));
        Assert.Matches(@">Data dodania</th><td[^>]*>2026-11-10</td>", mail.HtmlBody);
        // Pole techniczne jest szare (etykieta i wartość), pozostałe pola nie.
        Assert.Matches(@"<th[^>]*color:#777777;[^>]*>Data dodania</th><td[^>]*color:#777777;[^>]*>2026-11-10</td>", mail.HtmlBody);
        Assert.DoesNotMatch(@"<th[^>]*color:#777777;[^>]*>(Data|Zmiana)</th>", mail.HtmlBody);
        Assert.Contains("Data: 2026-11-13 (piątek, jutro)", mail.Body);
        // „Wykryto” to data techniczna: zostaje bez opisu.
        Assert.Contains("Wykryto: 2026-11-12 10:00\n", mail.Body.Replace("\r\n", "\n"));
        Assert.Matches(@">Wykryto</th><td[^>]*>2026-11-12 10:00</td>", mail.HtmlBody);
    }
}
