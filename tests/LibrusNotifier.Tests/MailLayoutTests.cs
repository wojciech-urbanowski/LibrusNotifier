using System.Text.RegularExpressions;
using LibrusNotifier.Data;
using LibrusNotifier.Digest;
using LibrusNotifier.Librus;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Tests;

/// <summary>
/// Układ HTML maili o elementach (czytany głównie na telefonie): zmiana w planie, wpis w terminarzu, wiadomość i ogłoszenie to karty,
/// reszta to linie „etykieta: wartość”; nigdzie nie ma kolumn o stałej szerokości.
/// </summary>
public sealed class MailLayoutTests
{
    private static readonly string[] Parent = ["rodzic@example.com"];

    // 2026-11-12 to czwartek, więc 2026-11-13 to „piątek, jutro”.
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 12, 10, 0, 0));

    private OutgoingMail Mail(string type, Field[] fields, Field[]? details = null, NotificationKind kind = NotificationKind.New)
    {
        var element = new ElementEntity
        {
            Type = type,
            Key = "k",
            DataJson = FieldJson.Serialize(fields),
            DetailsJson = details is null ? null : FieldJson.Serialize(details),
            VisibleToParent = true,
            VisibleToStudent = true,
            FirstSeenUtc = _clock.UtcNow.UtcDateTime
        };
        return MailTemplates.ForElement(element, Parent, _clock, kind: kind);
    }

    private static Field[] Substitution() =>
    [
        new("Zmiana", "zastępstwo"), new("Data", "2026-11-13"), new("Nr lekcji", "2"), new("Godziny", "08:55 - 09:40"),
        new("Przedmiot", "Język angielski"), new("Nauczyciel i sala", "Koźmińska Aleksandra"), new("Przedmiot (zmiana)", "Język angielski"),
        new("Nauczyciel (zmiana)", "Gubernat Julia -> Koźmińska Aleksandra"), new("Sala (zmiana)", "83a -> [brak]"), new("Data dodania", "2026-11-13 pt.")
    ];

    private static string Html(OutgoingMail mail) => Assert.IsType<string>(mail.HtmlBody);

    // ---------- zmiana w planie lekcji ----------

    [Fact]
    public void TimetableChangeIsOneCardWithDateBadgeLessonSubjectAndSwaps()
    {
        var html = Html(Mail(ElementTypes.TimetableChange, Substitution()));

        Assert.Single(Regex.Matches(html, "<table "));
        Assert.Contains("border-left:6px solid #b9e4c4;", html); // pasek w kolorze zastępstwa, jak w zestawieniach
        Assert.Contains("background:#b9e4c4;color:#1f1f1f;font-weight:600;\">zastępstwo</span>", html);
        Assert.Matches(@"font-size:16px;[^>]*><strong>2026-11-13 \(piątek, jutro\)</strong></div>", html);
        Assert.Contains("<strong>Lekcja 2</strong>", html);
        Assert.Contains("08:55 - 09:40", html);
        Assert.Contains("<strong>Język angielski</strong>", html);
        Assert.Contains("Nauczyciel i sala: Koźmińska Aleksandra", html);
        Assert.Contains("Co się zmieniło", html);
        Assert.Contains("<s style=\"color:#777777;\">Gubernat Julia</s> -&gt; <strong>Koźmińska Aleksandra</strong>", html);
        Assert.Contains("<s style=\"color:#777777;\">83a</s> -&gt; <strong>[brak]</strong>", html);
    }

    [Fact]
    public void SubjectWithoutChangeIsNotRepeatedInTheChanges()
    {
        var html = Html(Mail(ElementTypes.TimetableChange, Substitution()));

        // „Przedmiot (zmiana)” równy przedmiotowi nic nie zmienia, więc pokazany jest tylko raz (w karcie).
        Assert.Single(Regex.Matches(html, "Język angielski"));
        Assert.DoesNotContain("Przedmiot (zmiana)", html);
    }

    [Fact]
    public void ChangedSubjectIsShownAsSwap()
    {
        var fields = Substitution().Select(f => f.Label == "Przedmiot (zmiana)" ? new Field(f.Label, "Język angielski -> Język niemiecki") : f).ToArray();

        var html = Html(Mail(ElementTypes.TimetableChange, fields));

        Assert.Contains("<s style=\"color:#777777;\">Język angielski</s> -&gt; <strong>Język niemiecki</strong>", html);
    }

    [Fact]
    public void AddedDateIsInTheFooterNotInTheCard()
    {
        var html = Html(Mail(ElementTypes.TimetableChange, Substitution()));

        Assert.Contains("<div>Data dodania: 2026-11-13 pt.</div>", html);
        Assert.True(html.IndexOf("Data dodania", StringComparison.Ordinal) > html.IndexOf("</table>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("odwołane", "#ffd7a8")]
    [InlineData("Dzień wolny od zajęć", "#bcd9f5")]
    [InlineData("zmiana sali", "#e2e2e2")]
    public void BarColorFollowsTheKindOfChange(string change, string color)
    {
        var html = Html(Mail(ElementTypes.TimetableChange, [new("Zmiana", change), new("Data", "2026-11-13"), new("Przedmiot", "Matematyka")]));

        Assert.Contains($"border-left:6px solid {color};", html);
        Assert.Contains($"background:{color};color:#1f1f1f;font-weight:600;\">{change}</span>", html);
        Assert.DoesNotContain("Co się zmieniło", html); // bez pól „(zmiana)” nie ma nagłówka z listą zmian
    }

    [Theory]
    [InlineData("zastępstwo", EventCategory.Substitution)]
    [InlineData("Zastępstwo, zmiana sali", EventCategory.Substitution)]
    [InlineData("odwołane", EventCategory.Cancelled)]
    [InlineData("odwołanie zajęć", EventCategory.Cancelled)]
    [InlineData("Dzień wolny od zajęć", EventCategory.DayOff)]
    [InlineData("zmiana sali", EventCategory.Other)]
    [InlineData("", EventCategory.Other)]
    public void TimetableCategoryIsSharedWithTheDigest(string change, EventCategory expected) =>
        Assert.Equal(expected, DigestEventBuilder.TimetableCategory(change));

    [Fact]
    public void FieldTheCardDoesNotKnowIsKeptAsLine()
    {
        var html = Html(Mail(ElementTypes.TimetableChange, [.. Substitution(), new Field("Uwagi", "Bez zeszytu")]));

        Assert.Contains("<span style=\"color:#777777;\">Uwagi:</span> Bez zeszytu</div>", html);
    }

    // ---------- wpis w terminarzu ----------

    [Fact]
    public void ScheduleEntryShowsTheTitleOnceWhenSubjectIsTheSameText()
    {
        var title = "Zastępstwo z Aleksandra Koźmińska na lekcji nr: 2 (Język angielski)";

        var html = Html(Mail(ElementTypes.ScheduleEvent,
            [new("Data", "2026-11-13"), new("Nr lekcji", "2"), new("Przedmiot", title), new("Tytuł", title)]));

        // Poza <title> (temat maila) tytuł występuje w treści tylko raz.
        Assert.Single(Regex.Matches(html[html.IndexOf("<body", StringComparison.Ordinal)..], Regex.Escape(title)));
        Assert.Contains($"<strong>{title}</strong>", html);
        Assert.DoesNotContain("Przedmiot:", html);
        Assert.Contains("<strong>Lekcja 2</strong>", html);
        Assert.Contains("border-left:6px solid #e2e2e2;", html);
        Assert.Single(Regex.Matches(html, "<table "));
    }

    [Fact]
    public void ScheduleEntryWithDifferentSubjectAndDescriptionKeepsThemAsLines()
    {
        var html = Html(Mail(ElementTypes.ScheduleEvent,
            [new("Data", "2026-11-13"), new("Godzina", "10:00"), new("Przedmiot", "Matematyka"), new("Tytuł", "Sprawdzian"), new("Opis", "Ułamki")]));

        Assert.Contains("<strong>Sprawdzian</strong>", html);
        Assert.Contains("Przedmiot:</span> Matematyka</div>", html);
        Assert.Contains("Opis:</span> Ułamki</div>", html);
        Assert.Contains("10:00", html);
        Assert.DoesNotContain("Lekcja", html); // bez numeru lekcji same godziny
    }

    // ---------- wiadomość ----------

    private const string LongMessage = "Szanowni Państwo,\n\nw sprawie zwolnień:\n- pierwsza zasada\n- druga zasada\n\n\n   \nPozdrawiam\nJ.Selwesiuk";

    [Fact]
    public void MessageCardHasTopicSenderAndDateAndTheBodyIsBelowItInParagraphs()
    {
        var mail = Mail(ElementTypes.Message,
            [new("Nadawca", "Selwesiuk Jolanta (Selwesiuk Jolanta) [Nauczyciel]"), new("Temat", "Procedura zwalniania"), new("Data", "2026-11-12 18:56:07")],
            [new("Treść", LongMessage)]);
        var html = Html(mail);

        Assert.Single(Regex.Matches(html, "<table "));
        Assert.Contains("border-left:6px solid #bcd9f5;", html);
        Assert.Contains("font-weight:700;line-height:1.3;\">Procedura zwalniania</div>", html);
        Assert.Contains("od <strong>Selwesiuk Jolanta [Nauczyciel]</strong>", html);
        Assert.Contains("color:#777777;\">2026-11-12 18:56:07 (czwartek)</div>", html);

        // Treść jest pod kartą, bez etykiety, w trzech akapitach (kolejne puste linie to jeden odstęp), pojedyncze nowe linie jako <br>.
        Assert.True(html.IndexOf("Szanowni Państwo", StringComparison.Ordinal) > html.IndexOf("</table>", StringComparison.Ordinal));
        Assert.DoesNotContain(">Treść<", html);
        Assert.Equal(3, Regex.Matches(html, "<p style=\"margin:").Count);
        Assert.Contains("w sprawie zwolnień:<br>\n- pierwsza zasada<br>\n- druga zasada</p>", html);
        Assert.Contains("Pozdrawiam<br>\nJ.Selwesiuk</p>", html);

        // Wersja tekstowa i temat nadal mają nadawcę tak, jak podał Librus.
        Assert.Contains("Nadawca: Selwesiuk Jolanta (Selwesiuk Jolanta) [Nauczyciel]", mail.Body);
        Assert.Contains("Selwesiuk Jolanta (Selwesiuk Jolanta) [Nauczyciel]", mail.Subject);
    }

    [Fact]
    public void MessageBodyBreaksLongWordsInsteadOfWideningTheMail()
    {
        var html = Html(Mail(ElementTypes.Message, [new("Nadawca", "Anna"), new("Temat", "Link")],
            [new("Treść", "https://example.com/bardzo/dlugi/adres/bez/spacji/i/bez/konca/1234567890")]));

        Assert.Matches(@"overflow-wrap:anywhere;word-break:break-word;""[^>]*>\s*<p", html);
    }

    [Fact]
    public void LinkInMessageBodyIsClickableInHtmlAndPlainInText()
    {
        var mail = Mail(ElementTypes.Message, [new("Nadawca", "Anna"), new("Temat", "Konkurs")],
            [new("Treść", "Dzień dobry,\nDo 20 listopada pracujemy nad pracą.\n\nPrzesyłam link do konkursu\n\nhttps://liblink.pl/zNYWOlww7S\n\nJeśli Państwo wyrażają zgodę, proszę o pracę do 2026-11-23.")]);
        var html = Html(mail);

        Assert.Contains("<p style=\"margin:0 0 10px;\"><a href=\"https://liblink.pl/zNYWOlww7S\">https://liblink.pl/zNYWOlww7S</a></p>", html);
        Assert.Contains("proszę o pracę do 2026-11-23 (poniedziałek, za 11 dni).</p>", html); // reszta tekstu jest opisywana jak dotąd
        Assert.Single(Regex.Matches(html, "<a href="));
        Assert.Contains("https://liblink.pl/zNYWOlww7S", mail.Body);
        Assert.DoesNotContain("<a ", mail.Body);
    }

    [Fact]
    public void LinkWithDateInsideIsNotAnnotatedAndTextAroundItIs()
    {
        var html = Html(Mail(ElementTypes.Message, [new("Nadawca", "Anna"), new("Temat", "Wyjazd")],
            [new("Treść", "Zapisy do 2026-11-13: https://szkola.pl/wyjazd/2026-11-13/zapisy.")]));

        Assert.Contains("<strong>2026-11-13 (piątek, jutro)</strong>: <a href=\"https://szkola.pl/wyjazd/2026-11-13/zapisy\">https://szkola.pl/wyjazd/2026-11-13/zapisy</a>.</p>", html);
    }

    [Fact]
    public void AddressWithAmpersandIsEncodedInHrefAndText()
    {
        var html = Html(Mail(ElementTypes.Message, [new("Nadawca", "Anna"), new("Temat", "Ankieta")],
            [new("Treść", "https://ankieta.pl/?a=1&b=2")]));

        Assert.Contains("<a href=\"https://ankieta.pl/?a=1&amp;b=2\">https://ankieta.pl/?a=1&amp;b=2</a>", html);
    }

    [Fact]
    public void LinkInOtherTypeAndInCardLineIsClickableToo()
    {
        var homework = Html(Mail(ElementTypes.Homework, [new("Przedmiot", "Historia"), new("Temat", "Zobacz: www.szkola.pl/apel")]));
        var scheduleEntry = Html(Mail(ElementTypes.ScheduleEvent, [new("Data", "2026-11-13"), new("Tytuł", "Wycieczka"), new("Opis", "Trasa: https://mapa.pl/trasa")]));

        Assert.Contains("Temat:</span> Zobacz: <a href=\"https://www.szkola.pl/apel\">www.szkola.pl/apel</a></div>", homework);
        Assert.Contains("Opis:</span> Trasa: <a href=\"https://mapa.pl/trasa\">https://mapa.pl/trasa</a></div>", scheduleEntry);
    }

    [Fact]
    public void AttachmentOfAMessageStaysInTheCardAsLine()
    {
        var html = Html(Mail(ElementTypes.Message, [new("Nadawca", "Anna"), new("Temat", "Zdjęcia"), new("Załącznik", "tak")]));

        Assert.Contains("Załącznik:</span> tak</div>", html);
        Assert.Single(Regex.Matches(html, "<table "));
    }

    [Theory]
    [InlineData("Jan Kowalski (Jan Kowalski) [Nauczyciel]", "Jan Kowalski [Nauczyciel]")]
    [InlineData("Jan Kowalski (jan kowalski)", "Jan Kowalski")]
    [InlineData("Jan Kowalski (jkowalski) [Rodzic]", "Jan Kowalski (jkowalski) [Rodzic]")]
    [InlineData("Jan (Jan Kowalski)", "Jan (Jan Kowalski)")]
    [InlineData("Anna Nowak", "Anna Nowak")]
    public void RepeatedNameInParenthesesIsDropped(string sender, string expected) =>
        Assert.Equal(expected, MailTemplates.WithoutRepeatedName(sender));

    // ---------- ogłoszenie (układ jak wiadomości) ----------

    private const string AnnouncementBody = "Szanowni Państwo,\n\nzapraszamy do udziału:\n- w poniedziałek\n- we wtorek\n\n\nDyrekcja";

    [Fact]
    public void AnnouncementCardHasTitleAuthorAndDateAndTheBodyIsBelowItInParagraphs()
    {
        var html = Html(Mail(ElementTypes.Announcement,
            [new("Tytuł", "Tydzień Tolerancji i Życzliwości"), new("Autor", "Kowalska Anna (Kowalska Anna)"), new("Data", "2026-11-13"), new("Treść", AnnouncementBody)]));

        Assert.Single(Regex.Matches(html, "<table "));
        Assert.Contains("border-left:6px solid #bcd9f5;", html);
        Assert.Contains("font-weight:700;line-height:1.3;\">Tydzień Tolerancji i Życzliwości</div>", html);
        Assert.Contains("od <strong>Kowalska Anna</strong>", html);
        Assert.Contains("color:#777777;\">2026-11-13 (piątek)</div>", html); // data dodania: sam dzień tygodnia, bez „jutro” i bez pogrubienia

        // Treść jest pod kartą, bez etykiety, w trzech akapitach, pojedyncze nowe linie jako <br>; w karcie nie ma jej drugi raz.
        Assert.True(html.IndexOf("Szanowni Państwo", StringComparison.Ordinal) > html.IndexOf("</table>", StringComparison.Ordinal));
        Assert.DoesNotContain("Treść", html);
        Assert.DoesNotContain("Autor:", html);
        Assert.Equal(3, Regex.Matches(html, "<p style=\"margin:").Count);
        Assert.Contains("zapraszamy do udziału:<br>\n- w poniedziałek<br>\n- we wtorek</p>", html);
        Assert.Matches(@"overflow-wrap:anywhere;word-break:break-word;""[^>]*>\s*<p", html);
    }

    [Fact]
    public void AnnouncementLooksTheSameAsAMessageWithTheSameContent()
    {
        static string Content(string html) // wszystko między nagłówkiem a stopką
        {
            var start = html.IndexOf("</h1>", StringComparison.Ordinal);
            return html[start..html.IndexOf("margin:14px 0 0;", StringComparison.Ordinal)];
        }

        var message = Html(Mail(ElementTypes.Message,
            [new("Nadawca", "Kowalska Anna"), new("Temat", "Konkurs"), new("Data", "2026-11-13 10:22:34")], [new("Treść", AnnouncementBody)]));
        var announcement = Html(Mail(ElementTypes.Announcement,
            [new("Tytuł", "Konkurs"), new("Autor", "Kowalska Anna"), new("Data", "2026-11-13 10:22:34"), new("Treść", AnnouncementBody)]));

        Assert.Equal(Content(message), Content(announcement));
    }

    [Fact]
    public void AnnouncementWithoutBodyIsJustTheCard()
    {
        var html = Html(Mail(ElementTypes.Announcement, [new("Tytuł", "Apel"), new("Autor", "Dyrekcja")]));

        Assert.Single(Regex.Matches(html, "<table "));
        Assert.DoesNotContain("<p style=", html);
    }

    [Fact]
    public void LinkInAnnouncementBodyIsClickable()
    {
        var html = Html(Mail(ElementTypes.Announcement, [new("Tytuł", "Apel"), new("Treść", "Zobacz: www.szkola.pl/apel")]));

        Assert.Contains("Zobacz: <a href=\"https://www.szkola.pl/apel\">www.szkola.pl/apel</a></p>", html);
    }

    // ---------- reszta typów i układ ogólny ----------

    [Fact]
    public void OtherTypesAreLinesWithLabelAndValueInOneLine()
    {
        var html = Html(Mail(ElementTypes.Homework, [new("Przedmiot", "Historia"), new("Nauczyciel", "Nowak Jan")]));

        Assert.DoesNotContain("<table", html);
        Assert.Contains("<span style=\"color:#777777;\">Przedmiot:</span> Historia</div>", html);
        Assert.Contains("<span style=\"color:#777777;\">Nauczyciel:</span> Nowak Jan</div>", html);
    }

    [Fact]
    public void DetailsOfOtherTypesKeepTheirLabelAboveFullWidthText()
    {
        var html = Html(Mail(ElementTypes.Homework, [new("Przedmiot", "Historia")], [new("Opis", "Strona 10\n\nStrona 12")]));

        Assert.Matches(@">Opis</div>\s*<div[^>]*>\s*<p[^>]*>Strona 10</p>\s*<p[^>]*>Strona 12</p>", html);
    }

    [Fact]
    public void RemovedTimetableChangeShowsStatusLinesAboveTheCard()
    {
        var html = Html(Mail(ElementTypes.TimetableChange, Substitution(), kind: NotificationKind.Removed));

        var status = html.IndexOf("Status:</span>", StringComparison.Ordinal);
        Assert.True(status > 0 && status < html.IndexOf("<table ", StringComparison.Ordinal));
        Assert.Contains("usunięty z Librusa", html);
        Assert.Single(Regex.Matches(html, "<table "));
    }

    [Theory]
    [InlineData(ElementTypes.TimetableChange)]
    [InlineData(ElementTypes.ScheduleEvent)]
    [InlineData(ElementTypes.Message)]
    [InlineData(ElementTypes.Announcement)]
    [InlineData(ElementTypes.Attendance)]
    public void NoElementHasAFixedWidthColumnOrTableHeaders(string type)
    {
        var fields = type == ElementTypes.TimetableChange
            ? Substitution()
            : [new("Data", "2026-11-13"), new("Tytuł", "Coś"), new("Nadawca", "Anna"), new("Temat", "Temat"), new("Rodzaj", "nieobecność")];

        var html = Html(Mail(type, fields, type == ElementTypes.Message ? [new Field("Treść", "Tekst")] : null));

        Assert.DoesNotMatch(@"(?<![-\w])width:\d+px", html); // tylko procenty i max-width
        Assert.DoesNotContain("<th", html);
        Assert.DoesNotContain("<td style=\"border:1px solid #999999;padding:6px", html);
    }

    [Fact]
    public void HeadingAndFooterStayTheSameInEveryLayout()
    {
        var html = Html(Mail(ElementTypes.Message, [new("Nadawca", "Anna"), new("Temat", "Zebranie")], [new Field("Treść", "Tekst")]));

        Assert.Contains("<h1 style=\"margin:0 0 12px;font-size:18px;font-weight:600;\">Nowa wiadomość</h1>", html);
        Assert.Contains("<div>Widoczne na koncie: rodzica i ucznia</div>", html);
        Assert.Contains("<div>Wykryto: 2026-11-12 10:00</div>", html);
        Assert.Contains($"<div>{MailTemplates.FooterText}</div>", html);
    }
}
