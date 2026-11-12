using LibrusNotifier.Data;
using LibrusNotifier.Logging;
using LibrusNotifier.Timing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Processing;

/// <param name="Moved">Powiadomienia „nowy” zamienione na „zmiana terminu”.</param>
/// <param name="Removed">Utworzone powiadomienia o usunięciu.</param>
/// <param name="Cancelled">Zniknięcia elementów, o których nie zdążono jeszcze powiadomić jako o nowych (nic nie wychodzi).</param>
/// <param name="Suppressed">Zniknięcia pominięte jako podejrzanie liczne (możliwy błąd odpowiedzi Librusa).</param>
public sealed record ChangeDetectionResult(int Moved, int Removed, int Cancelled, int Suppressed)
{
    public static ChangeDetectionResult None { get; } = new(0, 0, 0, 0);
    public bool Any => Moved + Removed + Cancelled + Suppressed > 0;
}

/// <summary>
/// Wykrywa zmiany terminu i usunięcia wpisów (terminarz, zmiany planu, zadania domowe) na podstawie tego, co zniknęło z list.
/// Uruchamiany po sprawdzeniu kont, przed wysyłką powiadomień, więc zmiana terminu wychodzi jednym mailem zamiast „nowy wpis”.
/// <list type="bullet">
/// <item>Zniknięcie: element nie jest na liście żadnego z kont, na których był widoczny (<see cref="TrackedElements.IsGone"/>).
/// Brane są pod uwagę tylko wpisy z dzisiejszą lub późniejszą datą i tylko te, które lista na pewno obejmuje.</item>
/// <item>Zmiana terminu: jedno zniknięcie i jeden nowy (jeszcze niewysłany) wpis o tej samej treści, ale innej dacie
/// (<see cref="TrackedElements.PairKey"/>). Nowy wpis dostaje wtedy powiadomienie „zmiana terminu”, a zniknięty nie dostaje żadnego.</item>
/// <item>Usunięcie: zniknięcie bez pary. Gdy jest dokładnie jeden inny, istniejący wpis o tej samej treści, mail wspomina o możliwym przeniesieniu.</item>
/// </list>
/// </summary>
public sealed class ChangeDetector(NotifierDbContext db, Clock clock, IAppLog log, bool studentNotificationsEnabled)
{
    /// <summary>Więcej zniknięć w jednym cyklu to niemal na pewno błąd listy, a nie zmiany w terminarzu – nie wysyłamy wtedy nic.</summary>
    public const int MaxChangesPerCycle = 20;

    /// <summary>Wpis, którego nie widziano dłużej, a nadal ma przyszłą datę, zostałby wykryty już dawno; starszych nie wczytujemy.</summary>
    private static readonly TimeSpan MaxStaleness = TimeSpan.FromDays(90);

    private sealed record Tracked(ElementEntity Element, IReadOnlyDictionary<string, string> Fields, DateOnly Date, string? PairKey);

    public async Task<ChangeDetectionResult> DetectAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var now = clock.UtcNow.UtcDateTime;
        var baselines = await db.Baselines.AsNoTracking().ToListAsync(ct);
        if (baselines.Count == 0) return ChangeDetectionResult.None;
        var latestFetch = baselines.Max(b => b.LastFetchUtc);
        var oldest = now - MaxStaleness;
        var types = TrackedElements.Types.ToArray();

        // Tylko elementy, których nie było w najnowszym pobraniu żadnej listy; reszta jest na liście.
        var candidates = await db.Elements.Include(e => e.Notifications)
            .Where(e => types.Contains(e.Type) && e.GoneHandledUtc == null && e.LastSeenUtc < latestFetch && e.LastSeenUtc >= oldest)
            .ToListAsync(ct);

        var gone = new List<Tracked>();
        var changed = false;
        foreach (var element in candidates)
        {
            if (!TrackedElements.IsGone(element, baselines)) continue;
            var fields = TrackedElements.Fields(element);
            if (!TrackedElements.TryGetDate(element.Type, fields, out var date) || date < today)
            {
                // Przeszłość nie interesuje nikogo: oznaczamy po cichu, żeby nie wczytywać jej w kolejnych cyklach.
                element.GoneHandledUtc = now;
                changed = true;
                continue;
            }
            if (!TrackedElements.ListCovers(element.Type, fields, today)) continue;
            gone.Add(new Tracked(element, fields, date, TrackedElements.PairKey(element.Type, fields)));
        }
        if (gone.Count == 0)
        {
            if (changed) await db.SaveChangesAsync(ct);
            return ChangeDetectionResult.None;
        }

        if (gone.Count > MaxChangesPerCycle)
        {
            log.Warn($"Z list zniknęło naraz {gone.Count} wpisów z przyszłą datą - to więcej niż {MaxChangesPerCycle}, więc pomijam powiadomienia " +
                "o zmianach (możliwy błąd odpowiedzi Librusa). Wpisy, które wrócą na listę, są traktowane jak zwykle.");
            foreach (var item in gone) item.Element.GoneHandledUtc = now;
            await db.SaveChangesAsync(ct);
            return new ChangeDetectionResult(0, 0, 0, gone.Count);
        }

        var pairs = await PairWithNewElementsAsync(gone, today, ct);
        Dictionary<string, List<Tracked>>? current = null;
        int moved = 0, removed = 0, cancelled = 0;

        foreach (var item in gone)
        {
            var o = item.Element;
            o.GoneHandledUtc = now;
            pairs.TryGetValue(o.Id, out var pairedTo);

            // Podpowiedź „możliwe przeniesienie” dla usunięcia bez pary: jedyny inny istniejący wpis o tej samej treści.
            var hint = pairedTo;
            if (hint is null && item.PairKey is not null)
            {
                current ??= await LoadCurrentByPairKeyAsync(types, baselines, today, ct);
                var others = current.GetValueOrDefault(item.PairKey)?.Where(c => c.Date != item.Date && c.Element.Id != o.Id).ToList() ?? [];
                if (others.Count == 1) hint = others[0].Element;
            }

            foreach (var group in GroupsFor(o))
            {
                var original = o.Notifications.FirstOrDefault(n => n.Group == group && n.Kind == NotificationKind.New);
                if (original is { Status: NotificationStatus.Pending })
                {
                    // Nikt jeszcze nie dostał wiadomości o tym wpisie, więc nie ma czego odwoływać.
                    original.Status = NotificationStatus.Skipped;
                    original.LastError = "Wpis zniknął z listy przed wysłaniem powiadomienia.";
                    cancelled++;
                    continue;
                }

                var replacement = pairedTo?.Notifications.FirstOrDefault(n => n.Group == group && n.Kind == NotificationKind.New
                    && n.Status == NotificationStatus.Pending);
                if (replacement is not null)
                {
                    replacement.Kind = NotificationKind.Moved;
                    replacement.RelatedElementId = o.Id;
                    moved++;
                    continue;
                }

                if (o.Notifications.Any(n => n.Group == group && n.Kind == NotificationKind.Removed)) continue;
                o.Notifications.Add(new NotificationEntity
                {
                    Group = group, Kind = NotificationKind.Removed, Status = NotificationStatus.Pending, CreatedUtc = now, RelatedElementId = hint?.Id
                });
                removed++;
            }
        }

        await db.SaveChangesAsync(ct);
        var result = new ChangeDetectionResult(moved, removed, cancelled, 0);
        log.Info($"Zniknięcia z list: {gone.Count} (zmiany terminu {moved}, usunięcia {removed}, anulowane przed wysyłką {cancelled}).");
        return result;
    }

    /// <summary>Para zniknięty → nowy wpis (tylko jednoznaczna: jedno zniknięcie i jeden nowy wpis o tym samym kluczu).</summary>
    private async Task<Dictionary<int, ElementEntity>> PairWithNewElementsAsync(List<Tracked> gone, DateOnly today, CancellationToken ct)
    {
        var pairs = new Dictionary<int, ElementEntity>();
        if (gone.All(g => g.PairKey is null)) return pairs;

        var types = gone.Where(g => g.PairKey is not null).Select(g => g.Element.Type).Distinct().ToArray();
        var goneIds = gone.Select(g => g.Element.Id).ToHashSet();
        var pending = await db.Elements.Include(e => e.Notifications)
            .Where(e => types.Contains(e.Type) && e.Notifications.Any(n => n.Kind == NotificationKind.New && n.Status == NotificationStatus.Pending))
            .ToListAsync(ct);

        var newByKey = new Dictionary<string, List<Tracked>>(StringComparer.Ordinal);
        foreach (var element in pending.Where(e => !goneIds.Contains(e.Id)))
        {
            var fields = TrackedElements.Fields(element);
            if (!TrackedElements.TryGetDate(element.Type, fields, out var date) || date < today) continue;
            var key = TrackedElements.PairKey(element.Type, fields);
            if (key is null) continue;
            if (!newByKey.TryGetValue(key, out var list)) newByKey[key] = list = [];
            list.Add(new Tracked(element, fields, date, key));
        }

        foreach (var byKey in gone.Where(g => g.PairKey is not null).GroupBy(g => g.PairKey!, StringComparer.Ordinal))
        {
            var oldOnes = byKey.ToList();
            if (oldOnes.Count != 1 || !newByKey.TryGetValue(byKey.Key, out var newOnes)) continue;
            var candidates = newOnes.Where(n => n.Date != oldOnes[0].Date).ToList();
            if (candidates.Count == 1) pairs[oldOnes[0].Element.Id] = candidates[0].Element;
        }
        return pairs;
    }

    private async Task<Dictionary<string, List<Tracked>>> LoadCurrentByPairKeyAsync(string[] types, IReadOnlyList<BaselineEntity> baselines,
        DateOnly today, CancellationToken ct)
    {
        var result = new Dictionary<string, List<Tracked>>(StringComparer.Ordinal);
        var elements = await db.Elements.AsNoTracking().Where(e => types.Contains(e.Type) && e.GoneHandledUtc == null).ToListAsync(ct);
        foreach (var element in elements)
        {
            if (TrackedElements.IsGone(element, baselines)) continue;
            var fields = TrackedElements.Fields(element);
            var key = TrackedElements.PairKey(element.Type, fields);
            if (key is null || !TrackedElements.TryGetDate(element.Type, fields, out var date) || date < today) continue;
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(new Tracked(element, fields, date, key));
        }
        return result;
    }

    private IEnumerable<RecipientGroup> GroupsFor(ElementEntity element)
    {
        yield return RecipientGroup.Parents;
        if (element.VisibleToStudent && studentNotificationsEnabled) yield return RecipientGroup.Student;
    }
}
