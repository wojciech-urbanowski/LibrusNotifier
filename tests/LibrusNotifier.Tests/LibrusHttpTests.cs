using System.Net;
using LibrusApixNet;
using LibrusNotifier.Configuration;
using LibrusNotifier.Librus;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Tests;

public sealed class LibrusHttpTests
{
    private const string Panel = """
        <div id="graphic-menu"><ul>
        <li><a href="/wiadomosci3">Wiadomości</a><a class="button counter">2</a></li>
        <li><a href="/moje_zadania">Zadania</a><a class="button counter">0</a></li>
        <li><a href="/przegladaj_oceny/uczen">Oceny</a><a class="button counter">1</a></li>
        <li><a href="/terminarz">Terminarz</a><a class="button counter">0</a></li>
        <li><a href="/ogloszenia">Ogłoszenia</a><a class="button counter">0</a></li>
        <li><a href="/przegladaj_nb/uczen">Nieobecności</a><a class="button counter">0</a></li>
        </ul></div>
        """;

    private const string Received = """
        <table class="decorated stretch"><tbody><tr class="line0">
        <td></td><td></td><td><a href="/wiadomosci/1/5/42">Anna</a></td><td style="font-weight: bold">Spotkanie</td><td>2026-11-10</td><td></td>
        </tr></tbody></table>
        """;

    private const string Content = """
        <table class="stretch"><tr><td class="left">Anna</td></tr><tr><td class="left">Spotkanie</td></tr><tr><td class="left">2026-11-10</td></tr></table>
        <div class="container-message-content">Zebranie o 18:00.</div>
        """;

    private static readonly ClientOptions Options = new() { BaseUrl = "https://school.test", ApiUrl = "https://api.test" };

    /// <summary>Logowanie z przekierowaniami między hostami; ciasteczka sesji ustawiane są w krokach pośrednich.</summary>
    private static HttpResponseMessage Librus(RecordedRequest request, string panelPath)
    {
        var path = request.Uri.AbsolutePath;
        // Prawdziwe API Librusa na korzeniu (sonda przerwy technicznej) odpowiada 401, gdy działa.
        if (request.Uri.Host == "api.test" && path == "/")
            return MockHandler.Html("{\"Status\":\"Error\",\"Message\":\"Request is denied\"}", HttpStatusCode.Unauthorized, "application/json");
        if (path == "/loguj/portalRodzina") return MockHandler.Redirect("https://api.test/OAuth/Authorization?client_id=46&response_type=code");
        if (request.Uri.Host == "api.test" && path == "/OAuth/Authorization" && request.Method == HttpMethod.Get)
        {
            var response = MockHandler.Html("<html>login</html>");
            response.Headers.Add("Set-Cookie", "oauth_state=s1; Path=/; Secure");
            return response;
        }
        if (path == "/OAuth/Authorization" && request.Method == HttpMethod.Post)
            return request.Cookie.Contains("oauth_state=s1")
                ? MockHandler.Html("{\"status\":\"ok\",\"goTo\":\"/OAuth/Authorization/2FA\"}", mediaType: "application/json")
                : MockHandler.Html("{\"status\":\"error\",\"errors\":[{\"message\":\"brak stanu\"}]}", mediaType: "application/json");
        if (path == "/OAuth/Authorization/2FA") return MockHandler.Redirect("https://school.test/loguj/przekierowanie?code=abc");
        if (path == "/loguj/przekierowanie")
        {
            // Jak w prawdziwym Librusie: ostatni krok to strona 200 dla Portalu Rodzina (nie przekierowanie na panel).
            var response = MockHandler.Html("<script>window.parent.postMessage({\"command\":\"open_synergia_window\"}, 'https://portal.test');</script>");
            response.Headers.Add("Set-Cookie", "DZIENNIKSID=d1; Path=/; Secure; HttpOnly");
            response.Headers.Add("Set-Cookie", "SDZIENNIKSID=s1; Path=/; Secure; HttpOnly");
            return response;
        }
        // Panel dostępny jest tylko pod ścieżką właściwą dla typu konta; druga zwraca 403 „Brak dostępu”.
        if (path is "/rodzic/index" or "/uczen/index")
            return path == panelPath && request.Cookie.Contains("DZIENNIKSID=d1")
                ? MockHandler.Html(Panel)
                : MockHandler.Html("<h2 class='inside'>Brak dostępu</h2>", HttpStatusCode.Forbidden);
        if (path == "/wiadomosci/1/5") return MockHandler.Html(Received);
        if (path == "/wiadomosci/1/5/42") return MockHandler.Html(Content);
        return MockHandler.Html("nie znaleziono", HttpStatusCode.NotFound);
    }

    private sealed class Setup : IDisposable
    {
        public Setup(DateTime? start = null, TimeSpan? idleLimit = null, Func<Clock, RequestThrottle>? throttle = null,
            AccountKind account = AccountKind.Parent)
        {
            Clock = new FakeClock(start ?? new DateTime(2026, 11, 12, 10, 0, 0));
            Stats = new DailyStats(Clock.Today, Clock.Now);
            Guard = new DayGuard(Clock, Clock.Today, CancellationToken.None);
            Throttle = throttle?.Invoke(Clock) ?? Throttles.None(Clock);
            var panelPath = account == AccountKind.Parent ? "/rodzic/index" : "/uczen/index";
            Handler = new MockHandler(r => Librus(r, panelPath));
            Account = new LibrusAccount(account, new AccountCredentials("rodzic1", "Sekret&Haslo"), idleLimit ?? TimeSpan.FromMinutes(9),
                Clock, Guard, Log, Stats, Throttle, Options, () => Handler);
        }

        public FakeClock Clock { get; }
        public ListLog Log { get; } = new();
        public DailyStats Stats { get; }
        public DayGuard Guard { get; }
        public RequestThrottle Throttle { get; }
        public MockHandler Handler { get; }
        public LibrusAccount Account { get; }

        public void Dispose()
        {
            Account.Dispose();
            Guard.Dispose();
        }
    }

    [Fact]
    public async Task LoginFollowsRedirectsWithCookiesAndReadsPanelFirst()
    {
        using var s = new Setup();

        var counters = await s.Account.LoginAsync(CancellationToken.None);

        Assert.Equal([ModuleKind.Grades, ModuleKind.Messages], counters.NonZero.Order());
        var requests = s.Handler.Requests;
        Assert.Equal("/rodzic/index", requests[^1].Uri.AbsolutePath);
        Assert.Contains("DZIENNIKSID=d1", requests[^1].Cookie);
        Assert.DoesNotContain("DZIENNIKSID", requests.Single(r => r.Method == HttpMethod.Post).Cookie);
        var stats = s.Stats.Snapshot();
        Assert.Equal(requests.Count, stats.Requests[AccountKind.Parent]);
        Assert.Equal(1, stats.Logins[AccountKind.Parent]);
    }

    [Fact]
    public async Task StudentAccountReadsPanelFromStudentView()
    {
        using var s = new Setup(account: AccountKind.Student);

        var counters = await s.Account.LoginAsync(CancellationToken.None);

        Assert.Equal([ModuleKind.Grades, ModuleKind.Messages], counters.NonZero.Order());
        Assert.Equal("/uczen/index", s.Handler.Requests[^1].Uri.AbsolutePath);
        Assert.DoesNotContain(s.Handler.Requests, r => r.Uri.AbsolutePath == "/rodzic/index");
    }

    [Fact]
    public async Task ParentAccountNeverRequestsStudentView()
    {
        // Prawdziwy Librus odpowiada rodzicowi na /uczen/index kodem 403 „Brak dostępu”.
        using var s = new Setup();

        await s.Account.LoginAsync(CancellationToken.None);

        Assert.DoesNotContain(s.Handler.Requests, r => r.Uri.AbsolutePath == "/uczen/index");
        Assert.Equal("/rodzic/index", LibrusAccount.PanelPath(AccountKind.Parent));
        Assert.Equal("/uczen/index", LibrusAccount.PanelPath(AccountKind.Student));
    }

    [Fact]
    public async Task LoginHandshakeUsesShortGapAndPanelReadReturnsToRegularGap()
    {
        var delays = new List<TimeSpan>();
        using var s = new Setup(throttle: clock => new RequestThrottle(clock, new FixedRandom(0.5), delay: (d, _) =>
        {
            delays.Add(d);
            ((FakeClock)clock).Advance(d);
            return Task.CompletedTask;
        }));

        await s.Account.LoginAsync(CancellationToken.None);

        // Pierwsze żądanie nie ma poprzednika, kroki handshake'u czekają po 0,5 s, a odczyt panelu po logowaniu zwykłe 8 s.
        var requests = s.Handler.Requests;
        Assert.Equal("/rodzic/index", requests[^1].Uri.AbsolutePath);
        Assert.Equal(requests.Count - 1, delays.Count);
        Assert.All(delays.Take(delays.Count - 1), d => Assert.Equal(TimeSpan.FromSeconds(0.5), d));
        Assert.Equal(TimeSpan.FromSeconds(8), delays[^1]);
    }

    [Fact]
    public async Task FailedLoginAlsoReturnsToRegularGap()
    {
        var delays = new List<TimeSpan>();
        using var s = new Setup(throttle: clock => new RequestThrottle(clock, new FixedRandom(0.5), delay: (d, _) =>
        {
            delays.Add(d);
            ((FakeClock)clock).Advance(d);
            return Task.CompletedTask;
        }));
        var handler = new MockHandler(r => r.Method == HttpMethod.Post
            ? MockHandler.Html("{\"status\":\"error\",\"errors\":[{\"message\":\"zle haslo\"}]}", mediaType: "application/json")
            : MockHandler.Html("{}"));
        var account = new LibrusAccount(AccountKind.Student, new AccountCredentials("u", "p"), TimeSpan.FromMinutes(9), s.Clock, s.Guard,
            s.Log, s.Stats, s.Throttle, Options, () => handler);

        await Assert.ThrowsAsync<AuthorizationException>(() => account.LoginAsync(CancellationToken.None));
        delays.Clear();
        using var next = new LibrusAccount(AccountKind.Student, new AccountCredentials("u", "p"), TimeSpan.FromMinutes(9), s.Clock, s.Guard,
            s.Log, s.Stats, s.Throttle, Options, () => handler);
        await Assert.ThrowsAsync<AuthorizationException>(() => next.LoginAsync(CancellationToken.None));

        // Pierwsze żądanie kolejnego logowania znów czeka zwykły odstęp od końca poprzedniego.
        Assert.Equal(TimeSpan.FromSeconds(8), delays[0]);
        account.Dispose();
    }

    [Fact]
    public async Task HttpLogShowsCookieNamesButNeverValuesAndDescribesErrors()
    {
        var log = new ListLog();
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        using var guard = new DayGuard(clock, clock.Today, CancellationToken.None);
        var inner = new MockHandler(r => r.Uri.AbsolutePath switch
        {
            "/ok" => SetCookies(MockHandler.Html("ok"), "DZIENNIKSID=tajne1; Path=/; Secure; HttpOnly", "SDZIENNIKSID=tajne2; Path=/"),
            "/zakazane" => MockHandler.Html("<html>\n  <h1>Brak   dostępu</h1>\n</html>", HttpStatusCode.Forbidden),
            _ => MockHandler.Html("{\"Status\":\"Error\"}", HttpStatusCode.Unauthorized, "application/json")
        });
        var cookies = new CookieContainer();
        using var client = new HttpClient(new LibrusHttpHandler(inner, cookies, AccountKind.Parent, log,
            new DailyStats(clock.Today, clock.Now), guard, new SessionActivity(clock, TimeSpan.FromMinutes(9)), Throttles.None(clock)));

        (await client.GetAsync("https://school.test/ok")).Dispose();
        using var forbidden = new HttpRequestMessage(HttpMethod.Get, "https://school.test/zakazane");
        forbidden.Headers.TryAddWithoutValidation("Cookie", "DZIENNIKSID=tajne1; SDZIENNIKSID=tajne2");
        (await client.SendAsync(forbidden)).Dispose();
        (await client.GetAsync("https://api.test/")).Dispose();

        var lines = log.Messages().ToList();
        Assert.Contains(lines, l => l.StartsWith("HTTP rodzic GET school.test/ok -> 200 ") && l.Contains(", ustawia: DZIENNIKSID, SDZIENNIKSID"));
        Assert.Contains(lines, l => l == "HTTP rodzic szczegóły błędu 403: wysłane ciasteczka: DZIENNIKSID, SDZIENNIKSID; typ: text/html; treść: <html> <h1>Brak dostępu</h1> </html>");
        // Oczekiwane 401 na korzeniu API (sonda przerwy technicznej) nie dostaje opisu błędu.
        Assert.Single(lines, l => l.Contains("szczegóły błędu"));
        Assert.DoesNotContain(lines, l => l.Contains("tajne"));
    }

    private static HttpResponseMessage SetCookies(HttpResponseMessage response, params string[] values)
    {
        foreach (var value in values) response.Headers.Add("Set-Cookie", value);
        return response;
    }

    [Fact]
    public async Task RequestLogContainsMethodPathStatusButNoQueryOrCredentials()
    {
        using var s = new Setup();
        await s.Account.LoginAsync(CancellationToken.None);

        var httpLines = s.Log.Messages().Where(m => m.StartsWith("HTTP ", StringComparison.Ordinal)).ToList();
        Assert.Equal(s.Handler.Requests.Count, httpLines.Count);
        Assert.Contains(httpLines, l => l.StartsWith("HTTP rodzic POST api.test/OAuth/Authorization -> 200 ", StringComparison.Ordinal));
        Assert.Contains(httpLines, l => l.Contains("GET school.test/loguj/portalRodzina -> 302", StringComparison.Ordinal));
        Assert.All(httpLines, l =>
        {
            Assert.DoesNotContain("?", l);
            Assert.DoesNotContain("Sekret", l);
            Assert.DoesNotContain("rodzic1", l);
            Assert.DoesNotContain("d1", l);
        });
    }

    [Fact]
    public async Task ListsAndDetailsUseSessionWithoutRelogin()
    {
        using var s = new Setup();
        await s.Account.LoginAsync(CancellationToken.None);
        var before = s.Handler.Requests.Count;

        Assert.Null(await s.Account.EnsureSessionAsync(CancellationToken.None));
        var messages = await s.Account.FetchListAsync(ModuleKind.Messages, CancellationToken.None);
        var details = await s.Account.FetchDetailsAsync(Assert.Single(messages), CancellationToken.None);

        Assert.Equal(2, s.Handler.Requests.Count - before);
        Assert.Contains(details!, f => f is { Label: "Treść", Value: "Zebranie o 18:00." });
    }

    [Fact]
    public async Task IdleLimitBoundaryKeepsSessionAndLongerBreakLogsInAgain()
    {
        using var s = new Setup();
        await s.Account.LoginAsync(CancellationToken.None);
        await s.Account.FetchListAsync(ModuleKind.Messages, CancellationToken.None);

        s.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Null(await s.Account.EnsureSessionAsync(CancellationToken.None));

        await s.Account.FetchListAsync(ModuleKind.Messages, CancellationToken.None);
        s.Clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(1));
        var counters = await s.Account.EnsureSessionAsync(CancellationToken.None);

        Assert.NotNull(counters);
        Assert.Equal(2, s.Stats.Snapshot().Logins[AccountKind.Parent]);
        Assert.Equal("/rodzic/index", s.Handler.Requests[^1].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task OnlySuccessfulResponsesRefreshActivity(HttpStatusCode status, bool expiredAfterwards)
    {
        using var s = new Setup();
        var activity = new SessionActivity(s.Clock, TimeSpan.FromMinutes(9));
        using var client = new HttpClient(new LibrusHttpHandler(new MockHandler(_ => MockHandler.Html("x", status)),
            new CookieContainer(), AccountKind.Parent, s.Log, s.Stats, s.Guard, activity, s.Throttle));

        activity.LoggedIn();
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        (await client.GetAsync("https://school.test/x")).Dispose();
        s.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromTicks(1));

        Assert.Equal(expiredAfterwards, activity.IsExpired);
    }

    [Fact]
    public void ActivityIsTouchedOnlyInLoggedInSessionAndBoundaryIsInclusive()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var activity = new SessionActivity(clock, TimeSpan.FromMinutes(9));
        activity.Touch();
        Assert.False(activity.IsLoggedIn);
        Assert.True(activity.IsExpired);
        activity.LoggedIn();
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(activity.IsExpired);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(activity.IsExpired);
        activity.Clear();
        Assert.False(activity.IsLoggedIn);
    }

    [Fact]
    public async Task BadPasswordIsAuthorizationError()
    {
        using var s = new Setup();
        var handler = new MockHandler(r => r.Method == HttpMethod.Post
            ? MockHandler.Html("{\"status\":\"error\",\"errors\":[{\"message\":\"Nieprawidłowy login lub hasło\"}]}", mediaType: "application/json")
            : MockHandler.Html("{}"));
        using var account = new LibrusAccount(AccountKind.Student, new AccountCredentials("u", "p"), TimeSpan.FromMinutes(9), s.Clock, s.Guard,
            s.Log, s.Stats, s.Throttle, Options, () => handler);
        await Assert.ThrowsAsync<AuthorizationException>(() => account.LoginAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.FetchListAsync(ModuleKind.Grades, CancellationToken.None));
    }

    [Fact]
    public async Task DayChangeStopsRequestsBeforeTheyAreSent()
    {
        using var s = new Setup(start: new DateTime(2026, 11, 12, 23, 59, 59));
        await s.Account.LoginAsync(CancellationToken.None);
        var sent = s.Handler.Requests.Count;

        s.Clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Account.FetchListAsync(ModuleKind.Messages, s.Guard.Token));

        Assert.True(s.Guard.DayChanged);
        Assert.True(s.Guard.Token.IsCancellationRequested);
        Assert.Equal(sent, s.Handler.Requests.Count);
    }

    [Fact]
    public async Task RedirectsChangePostToGetExcept307()
    {
        var log = new ListLog();
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        using var guard = new DayGuard(clock, clock.Today, CancellationToken.None);
        var inner = new MockHandler(r => r.Uri.AbsolutePath switch
        {
            "/a" => MockHandler.Redirect("/b"),
            "/c" => MockHandler.Redirect("/d", HttpStatusCode.TemporaryRedirect),
            _ => MockHandler.Html("ok")
        });
        using var client = new HttpClient(new LibrusHttpHandler(inner, new CookieContainer(), AccountKind.Student, log,
            new DailyStats(clock.Today, clock.Now), guard, new SessionActivity(clock, TimeSpan.FromMinutes(9)), Throttles.None(clock)));

        (await client.PostAsync("https://school.test/a", new FormUrlEncodedContent([new("x", "1")]))).Dispose();
        (await client.PostAsync("https://school.test/c", new FormUrlEncodedContent([new("y", "2")]))).Dispose();

        Assert.Equal(HttpMethod.Get, inner.Requests[1].Method);
        Assert.Equal("", inner.Requests[1].Body);
        Assert.Equal(HttpMethod.Post, inner.Requests[3].Method);
        Assert.Equal("y=2", inner.Requests[3].Body);
    }
}
