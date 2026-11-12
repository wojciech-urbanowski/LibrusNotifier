using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Timing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Processing;

/// <param name="Baseline">Pierwsze pobranie listy modułu z tego konta: dane zapisane bez powiadomień.</param>
/// <param name="Created">Nowe elementy z powiadomieniem (puste przy zapisie początkowym).</param>
/// <param name="NewForStudent">Elementy, które dopiero teraz okazały się widoczne na koncie ucznia (powiadomienie dla ucznia).</param>
public sealed record ApplyResult(bool Baseline, int ListCount, IReadOnlyList<(ElementEntity Element, ObservedItem Item)> Created, int NewForStudent)
{
    public int NewCount => Created.Count;
}

/// <summary>
/// Porównuje pobraną listę z bazą. Element o nowym kluczu jest nowością (także edycja w Librusie), zniknięcie z listy jest ignorowane.
/// Rodzice dostają powiadomienie o każdym nowym elemencie z dowolnego konta, uczeń – o elementach widocznych na jego koncie.
/// Ten sam element z obu kont ma jeden wpis, a każda grupa odbiorców najwyżej jedno powiadomienie.
/// </summary>
public sealed class ElementStore(NotifierDbContext db, Clock clock, bool studentNotificationsEnabled)
{
    public async Task<bool> HasBaselineAsync(AccountKind account, ModuleKind module, CancellationToken ct) =>
        await db.Baselines.AnyAsync(b => b.Account == account && b.Module == module, ct);

    public async Task<IReadOnlySet<ModuleKind>> MissingBaselinesAsync(AccountKind account, CancellationToken ct)
    {
        var done = await db.Baselines.Where(b => b.Account == account).Select(b => b.Module).ToListAsync(ct);
        return KindNames.AllModules.Except(done).ToHashSet();
    }

    public async Task<ApplyResult> ApplyAsync(AccountKind account, ModuleKind module, IReadOnlyList<KeyedItem> items, CancellationToken ct)
    {
        var baseline = !await HasBaselineAsync(account, module, ct);
        var now = clock.UtcNow.UtcDateTime;
        var keys = items.Select(i => i.Key).Distinct().ToList();
        var existing = new Dictionary<string, ElementEntity>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(500))
            foreach (var element in await db.Elements.Include(e => e.Notifications).Where(e => chunk.Contains(e.Key)).ToListAsync(ct))
                existing[element.Key] = element;

        var created = new List<(ElementEntity, ObservedItem)>();
        var newForStudent = 0;
        foreach (var (item, key) in items)
        {
            if (existing.TryGetValue(key, out var element))
            {
                element.LastSeenUtc = now;
                element.GoneHandledUtc = null; // wrócił na listę (albo nigdy z niej nie zniknął)
                if (element.IsVisibleTo(account)) continue;
                element.MarkVisibleTo(account);
                if (!baseline && account == AccountKind.Student && AddNotification(element, RecipientGroup.Student, now)) newForStudent++;
                continue;
            }

            element = new ElementEntity
            {
                Type = item.Type,
                Key = key,
                DataJson = FieldJson.Serialize(item.Fields),
                FirstSeenUtc = now,
                LastSeenUtc = now
            };
            element.MarkVisibleTo(account);
            db.Elements.Add(element);
            existing[key] = element;
            if (baseline) continue;
            AddNotification(element, RecipientGroup.Parents, now);
            if (account == AccountKind.Student) AddNotification(element, RecipientGroup.Student, now);
            created.Add((element, item));
        }

        // Ten sam znacznik czasu dostają widziane elementy (LastSeenUtc) i pobranie listy: element, którego LastSeenUtc
        // jest starszy niż ostatnie pobranie, zniknął z listy (patrz DigestEventSource).
        var fetch = await db.Baselines.FirstOrDefaultAsync(b => b.Account == account && b.Module == module, ct);
        if (fetch is null) db.Baselines.Add(new BaselineEntity { Account = account, Module = module, CompletedUtc = now, LastFetchUtc = now });
        else fetch.LastFetchUtc = now;
        await db.SaveChangesAsync(ct);
        return new ApplyResult(baseline, items.Count, created, newForStudent);
    }

    public async Task SaveDetailsAsync(ElementEntity element, IReadOnlyList<Field> details, CancellationToken ct)
    {
        element.DetailsJson = FieldJson.Serialize(details);
        await db.SaveChangesAsync(ct);
    }

    private bool AddNotification(ElementEntity element, RecipientGroup group, DateTime now)
    {
        if (group == RecipientGroup.Student && !studentNotificationsEnabled) return false;
        if (element.Notifications.Any(n => n.Group == group && n.Kind != NotificationKind.Removed)) return false;
        element.Notifications.Add(new NotificationEntity { Group = group, Status = NotificationStatus.Pending, CreatedUtc = now });
        return true;
    }
}
