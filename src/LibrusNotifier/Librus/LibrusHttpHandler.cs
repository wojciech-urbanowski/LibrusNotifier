using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LibrusNotifier.Logging;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;

namespace LibrusNotifier.Librus;

/// <summary>
/// Transport HTTP klienta Librusa. Każde żądanie (także kolejne kroki przekierowań) jest zapisywane w logu
/// (konto, metoda, host i ścieżka bez zapytania, ciasteczek i danych logowania, kod odpowiedzi, czas trwania)
/// i czeka na minimalny odstęp od poprzedniego żądania (<see cref="RequestThrottle"/>, wspólny dla obu kont).
/// Przed i po każdym żądaniu sprawdzana jest zmiana dnia. Przekierowania są obsługiwane tutaj, żeby ciasteczka
/// z każdego kroku trafiały do kontenera sesji (biblioteka przy własnym handlerze nie obsługuje ich sama).
/// </summary>
public sealed partial class LibrusHttpHandler : DelegatingHandler
{
    public const int MaxRedirects = 10;
    private const int MaxErrorBodyBuffer = 64 * 1024;
    private const int MaxErrorBodyLogged = 300;

    /// <summary>Limit czasu pojedynczego żądania sieciowego (bez oczekiwania na odstęp między żądaniami).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly CookieContainer _cookies;
    private readonly AccountKind _account;
    private readonly IAppLog _log;
    private readonly DailyStats _stats;
    private readonly DayGuard _dayGuard;
    private readonly SessionActivity _activity;
    private readonly RequestThrottle _throttle;
    private int _loginSteps = -1; // -1: poza logowaniem; 0: przed pierwszym żądaniem logowania; >0: kolejne kroki

    public LibrusHttpHandler(HttpMessageHandler inner, CookieContainer cookies, AccountKind account, IAppLog log, DailyStats stats,
        DayGuard dayGuard, SessionActivity activity, RequestThrottle throttle) : base(inner)
    {
        _cookies = cookies;
        _account = account;
        _log = log;
        _stats = stats;
        _dayGuard = dayGuard;
        _activity = activity;
        _throttle = throttle;
    }

    /// <summary>Transport bez automatycznych ciasteczek i przekierowań (obsługuje je <see cref="LibrusHttpHandler"/>).</summary>
    public static HttpMessageHandler CreateDefaultInner() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        for (var redirects = 0; ; redirects++)
        {
            var response = await SendOnceAsync(current, cancellationToken);
            var target = redirects < MaxRedirects ? RedirectTarget(response, current.RequestUri!) : null;
            if (target is null)
            {
                if (response.IsSuccessStatusCode) _activity.Touch();
                return response;
            }

            var next = CreateRedirectRequest(current, response.StatusCode, target);
            response.Dispose();
            if (!ReferenceEquals(current, request))
            {
                if (ReferenceEquals(current.Content, next.Content)) current.Content = null;
                current.Dispose();
            }
            current = next;
        }
    }

    /// <summary>
    /// Początek logowania. Pierwsze żądanie czeka zwykły odstęp od poprzedniego ruchu, a kolejne kroki handshake'u
    /// (także przekierowania) tylko krótki odstęp logowania (<see cref="RequestThrottle.LoginMaxGap"/>).
    /// </summary>
    public void BeginLogin() => _loginSteps = 0;

    /// <summary>Koniec logowania: następne żądania (odczyt danych) wracają do zwykłego odstępu.</summary>
    public void EndLogin() => _loginSteps = -1;

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _dayGuard.Check();
        BrowserIdentity.Apply(request.Headers);
        HttpResponseMessage response;
        var uri = request.RequestUri!;
        var loginStep = _loginSteps > 0;
        if (_loginSteps >= 0) _loginSteps++;
        using (var slot = await _throttle.AcquireAsync(cancellationToken, loginStep))
        {
            _dayGuard.Check();
            var target = $"{request.Method} {uri.Host}{uri.AbsolutePath}";
            var waited = slot.Waited > TimeSpan.Zero
                ? string.Create(CultureInfo.InvariantCulture, $" (odstęp: czekano {slot.Waited.TotalSeconds:0.0} s)")
                : "";
            var stopwatch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                response = await base.SendAsync(request, timeout.Token);
            }
            catch (Exception e)
            {
                _stats.RecordRequest(_account, failed: true);
                _log.Info($"HTTP {_account.DisplayName()} {target} -> błąd {e.GetType().Name} {stopwatch.ElapsedMilliseconds} ms{waited}");
                if (e is OperationCanceledException && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TaskCanceledException($"Librus nie odpowiedział w ciągu {RequestTimeout.TotalSeconds:0} s.", new TimeoutException());
                throw;
            }
            var status = (int)response.StatusCode;
            _stats.RecordRequest(_account, failed: status >= 400);
            var setCookies = SetCookieNames(response);
            var sets = setCookies.Length > 0 ? $", ustawia: {setCookies}" : "";
            _log.Info($"HTTP {_account.DisplayName()} {target} -> {status} {stopwatch.ElapsedMilliseconds} ms{sets}{waited}");
        }
        StoreCookies(response, uri);
        try
        {
            // Oczekiwane 401 na korzeniu API (sonda przerwy technicznej) nie jest błędem.
            if (response.StatusCode >= HttpStatusCode.BadRequest && !(response.StatusCode == HttpStatusCode.Unauthorized && uri.AbsolutePath == "/"))
                await LogErrorDetailsAsync(request, response, cancellationToken);
            _dayGuard.Check();
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }

    /// <summary>
    /// Diagnostyka odpowiedzi z błędem: nazwy (bez wartości) wysłanych ciasteczek i początek treści.
    /// Hasła są maskowane przez log, a treść jest ucinana.
    /// </summary>
    private async Task LogErrorDetailsAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var sent = request.Headers.TryGetValues("Cookie", out var cookieValues) ? CookieNames(cookieValues.SelectMany(v => v.Split(';'))) : "brak";
        string body;
        try
        {
            await response.Content.LoadIntoBufferAsync(MaxErrorBodyBuffer, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            body = $"(nie udało się odczytać treści: {e.GetType().Name})";
        }
        body = WhitespaceRuns().Replace(body, " ").Trim();
        if (body.Length > MaxErrorBodyLogged) body = body[..MaxErrorBodyLogged] + "…";
        var type = response.Content.Headers.ContentType?.MediaType ?? "brak";
        _log.Info($"HTTP {_account.DisplayName()} szczegóły błędu {(int)response.StatusCode}: wysłane ciasteczka: {sent}; typ: {type}; treść: {body}");
    }

    private static string SetCookieNames(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? CookieNames(values) : "";

    private static string CookieNames(IEnumerable<string> cookies) =>
        string.Join(", ", cookies.Select(c => c.Split(';')[0].Split('=')[0].Trim()).Where(n => n.Length > 0).Distinct());

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    private void StoreCookies(HttpResponseMessage response, Uri uri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var value in values)
        {
            try
            {
                _cookies.SetCookies(uri, value);
            }
            catch (CookieException)
            {
                // Niepoprawne ciasteczko jest pomijane, jak w standardowym transporcie .NET.
            }
        }
    }

    private static Uri? RedirectTarget(HttpResponseMessage response, Uri requestUri)
    {
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            return null;
        var location = response.Headers.Location;
        if (location is null) return null;
        var target = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        if (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp) return null;
        // Jak w .NET: bez przejścia z HTTPS na HTTP.
        if (requestUri.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp) return null;
        return target;
    }

    private HttpRequestMessage CreateRedirectRequest(HttpRequestMessage previous, HttpStatusCode status, Uri target)
    {
        var keepMethod = status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            || previous.Method == HttpMethod.Get || previous.Method == HttpMethod.Head;
        var method = keepMethod ? previous.Method : HttpMethod.Get;
        var next = new HttpRequestMessage(method, target) { Version = previous.Version, VersionPolicy = previous.VersionPolicy };
        if (keepMethod && method != HttpMethod.Get && method != HttpMethod.Head) next.Content = previous.Content;
        foreach (var header in previous.Headers)
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;
            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var cookieHeader = _cookies.GetCookieHeader(target);
        if (cookieHeader.Length > 0) next.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        return next;
    }
}
