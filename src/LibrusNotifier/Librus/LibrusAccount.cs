using System.Net;
using LibrusApixNet;
using LibrusNotifier.Configuration;
using LibrusNotifier.Logging;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Librus;

/// <summary>
/// Konto Librusa z sesją trzymaną wyłącznie w pamięci. Token jest używany ponownie w obrębie cyklu,
/// a po przerwie dłuższej niż limit bezczynności token i ciasteczka są usuwane i następuje ponowne logowanie.
/// </summary>
public sealed class LibrusAccount : IAccountGateway
{
    private readonly AccountCredentials _credentials;
    private readonly Clock _clock;
    private readonly DayGuard _dayGuard;
    private readonly IAppLog _log;
    private readonly DailyStats _stats;
    private readonly ClientOptions _options;
    private readonly Func<HttpMessageHandler> _innerHandlerFactory;
    private readonly SessionActivity _activity;
    private readonly RequestThrottle _throttle;
    private Client? _client;

    /// <param name="throttle">Odstęp między żądaniami – ten sam obiekt dla obu kont.</param>
    public LibrusAccount(AccountKind account, AccountCredentials credentials, TimeSpan idleLimit, Clock clock, DayGuard dayGuard,
        IAppLog log, DailyStats stats, RequestThrottle throttle, ClientOptions? options = null, Func<HttpMessageHandler>? innerHandlerFactory = null)
    {
        Account = account;
        _credentials = credentials;
        _clock = clock;
        _dayGuard = dayGuard;
        _log = log;
        _stats = stats;
        // Limit pojedynczego żądania pilnuje LibrusHttpHandler; limit klienta obejmuje też oczekiwanie na odstępy
        // między krokami przekierowań, więc jest tylko zabezpieczeniem przed zawieszeniem.
        _options = (options ?? new ClientOptions()) with { TimeProvider = new ClockTimeProvider(clock), Timeout = TimeSpan.FromMinutes(5) };
        // Portal Rodzina kieruje rodzica na /rodzic/index, a ucznia na /uczen/index. Konto rodzica nie ma wstępu do widoku
        // ucznia (403 „Brak dostępu”); ścieżki modułów (oceny, ogłoszenia, terminarz itd.) są wspólne dla obu kont.
        _options = _options with { IndexUrl = _options.BaseUrl + PanelPath(account) };
        _throttle = throttle;
        _innerHandlerFactory = innerHandlerFactory ?? LibrusHttpHandler.CreateDefaultInner;
        _activity = new SessionActivity(clock, idleLimit);
    }

    /// <summary>Ścieżka panelu startowego (z licznikami powiadomień) dla danego typu konta.</summary>
    public static string PanelPath(AccountKind account) => account == AccountKind.Parent ? "/rodzic/index" : "/uczen/index";

    public AccountKind Account { get; }

    private string Name => $"Konto {Account.DisplayName()}";

    private Client Client => _client ?? throw new InvalidOperationException("Brak sesji Librusa.");

    public async Task<PanelCounters> LoginAsync(CancellationToken cancellationToken)
    {
        Reset();
        var cookies = new CookieContainer();
        var handler = new LibrusHttpHandler(_innerHandlerFactory(), cookies, Account, _log, _stats, _dayGuard, _activity, _throttle);
        _client = new Client(token: null, options: _options, handler: handler, cookies: cookies);
        // Samo logowanie (handshake OAuth) ma krótszy odstęp między żądaniami; odczyt panelu po nim – zwykły.
        handler.BeginLogin();
        try
        {
            await _client.GetTokenAsync(_credentials.Login, _credentials.Password, cancellationToken);
        }
        catch
        {
            Reset();
            throw;
        }
        finally
        {
            handler.EndLogin();
        }
        _activity.LoggedIn();
        _stats.RecordLogin(Account);
        _log.Info($"{Name}: zalogowano.");
        var counters = PanelCounters.From(await Notifications.GetNewTokenNotificationAmountsAsync(Client, cancellationToken));
        _log.Info($"{Name}: panel powiadomień: {counters}.");
        return counters;
    }

    public async Task<PanelCounters?> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && !_activity.IsExpired) return null;
        if (_client is not null)
            _log.Info($"{Name}: przerwa dłuższa niż limit bezczynności ({_activity.IdleLimit.TotalMinutes:0.##} min) - ponowne logowanie.");
        return await LoginAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ObservedItem>> FetchListAsync(ModuleKind module, CancellationToken cancellationToken) =>
        LibrusLists.FetchAsync(Client, module, _clock.Today, cancellationToken);

    public bool HasDetails(ObservedItem item) => LibrusLists.HasDetails(item);

    public Task<IReadOnlyList<Field>?> FetchDetailsAsync(ObservedItem item, CancellationToken cancellationToken) =>
        LibrusLists.FetchDetailsAsync(Client, item, cancellationToken);

    public void Invalidate() => Reset();

    public void Dispose() => Reset();

    private void Reset()
    {
        _activity.Clear();
        _client?.Dispose();
        _client = null;
    }
}
