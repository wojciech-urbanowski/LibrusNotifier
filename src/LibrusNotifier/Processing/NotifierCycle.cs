using LibrusApixNet;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Reporting;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Processing;

/// <summary>
/// Jeden cykl sprawdzeń (ping). Dla każdego konta niezależnie: nowe logowanie i odczyt panelu, potem listy modułów
/// z niezerowym licznikiem (oraz przy przeglądzie – wszystkie), porównanie z bazą i szczegóły nowych elementów.
/// Na końcu wysyłka oczekujących powiadomień (także gdy liczniki są zerowe).
/// </summary>
public sealed class NotifierCycle
{
    public const int PingsPerFullReview = 3;
    public static readonly TimeSpan MinFullReviewInterval = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IAccountGateway> _accounts;
    private readonly Func<NotifierDbContext> _dbFactory;
    private readonly NotificationDispatcher _dispatcher;
    private readonly IMailSender _sender;
    private readonly AdminReporter _admin;
    private readonly Clock _clock;
    private readonly IAppLog _log;
    private readonly DailyStats _stats;
    private readonly bool _studentNotifications;
    private readonly Dictionary<AccountKind, HashSet<ModuleKind>> _carryOver = [];
    private int _pingNumber;
    private int _pingsSinceReview;
    private DateTimeOffset? _lastFullReview;

    public NotifierCycle(IReadOnlyList<IAccountGateway> accounts, Func<NotifierDbContext> dbFactory, NotificationDispatcher dispatcher,
        IMailSender sender, AdminReporter admin, Clock clock, IAppLog log, DailyStats stats, bool studentNotifications)
    {
        _accounts = accounts;
        _dbFactory = dbFactory;
        _dispatcher = dispatcher;
        _sender = sender;
        _admin = admin;
        _clock = clock;
        _log = log;
        _stats = stats;
        _studentNotifications = studentNotifications;
    }

    /// <summary>
    /// Wykonuje ping. Przegląd wszystkich list odbywa się w pierwszym pingu po starcie procesu, potem co
    /// <see cref="PingsPerFullReview"/>. ping, ale nie częściej niż raz na godzinę (licznik w pamięci procesu),
    /// oraz zawsze przy <paramref name="forceFullReview"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">Zmiana dnia lub zamknięcie aplikacji.</exception>
    public async Task RunAsync(bool forceFullReview, CancellationToken cancellationToken)
    {
        _pingNumber++;
        _pingsSinceReview++;
        var now = _clock.UtcNow;
        var fullReview = forceFullReview || _pingNumber == 1 ||
            (_pingsSinceReview >= PingsPerFullReview && (_lastFullReview is null || now - _lastFullReview >= MinFullReviewInterval));
        if (fullReview)
        {
            _pingsSinceReview = 0;
            _lastFullReview = now;
        }
        _stats.RecordPing(fullReview);
        _log.Info($"Ping #{_pingNumber}{(fullReview ? " z przeglądem wszystkich list" : "")}.");

        try
        {
            foreach (var account in _accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CheckAccountAsync(account, fullReview, cancellationToken);
            }

            await using var db = _dbFactory();
            await DetectChangesAsync(db, cancellationToken);
            try
            {
                await _dispatcher.DispatchAsync(db, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _log.Error($"Błąd wysyłki powiadomień: {e}");
                await _admin.ReportCriticalAsync($"dispatch|{e.GetType().FullName}|{e.Message}", "błąd wysyłki powiadomień", $"Błąd: {e.Message}");
            }
        }
        finally
        {
            await _sender.CloseAsync();
        }
    }

    /// <summary>
    /// Zmiany terminu i usunięcia (po sprawdzeniu kont, przed wysyłką, żeby zmiana terminu wyszła jednym mailem zamiast „nowy wpis”).
    /// Błąd wykrywania nie wstrzymuje wysyłki zwykłych powiadomień.
    /// </summary>
    private async Task DetectChangesAsync(NotifierDbContext db, CancellationToken ct)
    {
        try
        {
            var result = await new ChangeDetector(db, _clock, _log, _studentNotifications).DetectAsync(ct);
            _stats.RecordChanges(result.Moved, result.Removed);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Error($"Błąd wykrywania zmian na listach: {e}");
            await _admin.ReportCriticalAsync($"changes|{e.GetType().FullName}|{e.Message}", "błąd wykrywania zmian na listach", $"Błąd: {e.Message}");
        }
    }

    private async Task CheckAccountAsync(IAccountGateway account, bool fullReview, CancellationToken ct)
    {
        var name = account.Account.DisplayName();
        var carryOver = _carryOver.TryGetValue(account.Account, out var set) ? set : _carryOver[account.Account] = [];
        try
        {
            var counters = await account.LoginAsync(ct);
            await using var db = _dbFactory();
            var store = new ElementStore(db, _clock, _studentNotifications);

            var queue = new SortedSet<ModuleKind>(counters.NonZero);
            if (fullReview) queue.UnionWith(KindNames.AllModules);
            queue.UnionWith(carryOver);
            var missingBaselines = await store.MissingBaselinesAsync(account.Account, ct);
            if (missingBaselines.Count > 0)
                _log.Info($"Konto {name}: pierwsze pobranie list ({string.Join(", ", missingBaselines.Order().Select(m => m.DisplayName()))}) - zapis bez powiadomień.");
            queue.UnionWith(missingBaselines);
            carryOver.UnionWith(queue);
            if (queue.Count == 0) _log.Info($"Konto {name}: brak nowości wg panelu.");

            var fetches = new Dictionary<ModuleKind, int>();
            while (queue.Count > 0)
            {
                var module = queue.Min;
                queue.Remove(module);

                var relogin = await account.EnsureSessionAsync(ct);
                if (relogin is not null)
                {
                    // Liczniki po wymuszonym logowaniu też wzbudzają pobranie list.
                    foreach (var changed in relogin.NonZero.Where(m => m != module && fetches.GetValueOrDefault(m) < 2))
                    {
                        queue.Add(changed);
                        carryOver.Add(changed);
                    }
                }

                ct.ThrowIfCancellationRequested();
                var items = await account.FetchListAsync(module, ct);
                fetches[module] = fetches.GetValueOrDefault(module) + 1;
                var result = await store.ApplyAsync(account.Account, module, ElementKeys.Assign(items), ct);
                LogResult(name, module, result);

                foreach (var (element, item) in result.Created.Where(c => account.HasDetails(c.Item)))
                {
                    var detailsRelogin = await account.EnsureSessionAsync(ct);
                    if (detailsRelogin is not null)
                        foreach (var changed in detailsRelogin.NonZero.Where(m => fetches.GetValueOrDefault(m) < 2))
                        {
                            queue.Add(changed);
                            carryOver.Add(changed);
                        }
                    var details = await account.FetchDetailsAsync(item, ct);
                    if (details is not null) await store.SaveDetailsAsync(element, details, ct);
                }
                carryOver.Remove(module);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            account.Invalidate();
            var (category, librusError) = Classify(e);
            var message = $"Konto {name}: sprawdzanie przerwane ({category}): {Describe(e)}. Kolejna próba w następnym pingu.";
            _log.Error(librusError ? message : message + "\n" + e);
            await _admin.ReportCriticalAsync($"librus|{account.Account}|{e.GetType().FullName}|{e.Message}",
                $"{category} (konto {name})",
                $"Sprawdzanie konta {name} zostało przerwane.\nRodzaj: {category}\nBłąd: {Describe(e)}\n\nKolejna próba nastąpi w następnym pingu.");
        }
    }

    private void LogResult(string accountName, ModuleKind module, ApplyResult result)
    {
        if (result.Baseline)
        {
            _stats.RecordBaseline(result.ListCount);
            _log.Info($"Konto {accountName}, {module.DisplayName()}: {result.ListCount} na liście - zapis początkowy bez powiadomień.");
            return;
        }
        foreach (var group in result.Created.GroupBy(c => c.Element.Type))
            _stats.RecordNewElements(group.Key, group.Count());
        _log.Info($"Konto {accountName}, {module.DisplayName()}: {result.ListCount} na liście, nowych {result.NewCount}" +
            (result.NewForStudent > 0 ? $", nowo widocznych dla ucznia {result.NewForStudent}" : "") + ".");
    }

    private static (string Category, bool LibrusError) Classify(Exception e) => e switch
    {
        AuthorizationException => ("błąd logowania", true),
        MaintenanceException => ("przerwa techniczna Librusa", true),
        TokenException or TokenKeyException => ("sesja odrzucona przez Librusa", true),
        ParseException => ("nieoczekiwana odpowiedź serwera", true),
        LibrusException => ("błąd Librusa", true),
        HttpRequestException { StatusCode: not null } => ("błąd odpowiedzi serwera", true),
        HttpRequestException => ("brak dostępu do Librusa (sieć)", true),
        OperationCanceledException => ("przekroczony czas oczekiwania na Librusa", true),
        System.Text.Json.JsonException => ("nieoczekiwana odpowiedź serwera", true),
        _ => ("nieoczekiwany błąd", false)
    };

    public static string Describe(Exception e)
    {
        var text = $"{e.GetType().Name}: {e.Message}";
        if (e.InnerException is { } inner && !e.Message.Contains(inner.Message, StringComparison.Ordinal))
            text += $" ({inner.GetType().Name}: {inner.Message})";
        return text;
    }
}
