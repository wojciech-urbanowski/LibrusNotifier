using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Calendar;

/// <summary>
/// Ferie i wakacje z OpenHolidays API z lokalną pamięcią podręczną na dysku, odświeżaną co dobę.
/// Gdy API jest niedostępne, używany jest istniejący cache; gdy go brak, ferie i wakacje są pomijane.
/// </summary>
public sealed class SchoolHolidayProvider(string cacheFile, Clock clock, IAppLog log, HttpMessageHandler? handler = null)
{
    public static readonly Uri DefaultBaseUrl = new("https://openholidaysapi.org/");
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true };

    public Uri BaseUrl { get; init; } = DefaultBaseUrl;

    public async Task<IReadOnlyList<SchoolHolidayPeriod>> LoadAsync(string subdivisionCode, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var cache = ReadCache();
        var usable = cache is not null && cache.Subdivision == subdivisionCode;
        if (usable && clock.UtcNow - cache!.FetchedAtUtc < RefreshInterval && cache.ValidFrom <= today && cache.ValidTo >= today.AddDays(1))
        {
            log.Info($"Ferie i wakacje: cache z {clock.ToLocal(cache.FetchedAtUtc):yyyy-MM-dd HH:mm} ({cache.Periods.Count} okresów).");
            return cache.Periods;
        }

        var validFrom = today.AddDays(-31);
        var validTo = today.AddDays(366);
        try
        {
            var periods = await FetchAsync(subdivisionCode, validFrom, validTo, cancellationToken);
            log.Info($"Ferie i wakacje: pobrano z OpenHolidays API ({periods.Count} okresów, {subdivisionCode}).");
            try
            {
                WriteCache(new CacheFile
                {
                    Subdivision = subdivisionCode,
                    FetchedAtUtc = clock.UtcNow,
                    ValidFrom = validFrom,
                    ValidTo = validTo,
                    Periods = periods
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Nie można zapisać cache ferii i wakacji: {e.Message}");
            }
            return periods;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException
            || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (usable)
            {
                log.Warn($"OpenHolidays API niedostępne ({e.Message}); używam cache ferii i wakacji z {clock.ToLocal(cache!.FetchedAtUtc):yyyy-MM-dd HH:mm}.");
                return cache.Periods;
            }
            log.Warn($"OpenHolidays API niedostępne ({e.Message}) i brak cache ferii i wakacji; ferie i wakacje są pomijane (liczą się tylko weekendy i święta).");
            return [];
        }
    }

    private async Task<List<SchoolHolidayPeriod>> FetchAsync(string subdivisionCode, DateOnly validFrom, DateOnly validTo, CancellationToken ct)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        BrowserIdentity.Apply(http.DefaultRequestHeaders);
        var query = string.Create(CultureInfo.InvariantCulture,
            $"SchoolHolidays?countryIsoCode=PL&subdivisionCode={Uri.EscapeDataString(subdivisionCode)}&languageIsoCode=PL&validFrom={validFrom:yyyy-MM-dd}&validTo={validTo:yyyy-MM-dd}");
        using var response = await http.GetAsync(new Uri(BaseUrl, query), ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var items = await JsonSerializer.DeserializeAsync<List<ApiHoliday>>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Pusta odpowiedź OpenHolidays API.");
        return items
            .Select(i => new SchoolHolidayPeriod(i.StartDate, i.EndDate,
                i.Name?.FirstOrDefault(n => n.Language == "PL")?.Text ?? i.Name?.FirstOrDefault()?.Text ?? "ferie"))
            .Where(p => p.End >= p.Start)
            .OrderBy(p => p.Start)
            .ToList();
    }

    private CacheFile? ReadCache()
    {
        if (!File.Exists(cacheFile)) return null;
        try
        {
            using var stream = File.OpenRead(cacheFile);
            var cache = JsonSerializer.Deserialize<CacheFile>(stream);
            return cache is { Version: CacheFile.CurrentVersion } ? cache : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Warn($"Nie można odczytać cache ferii i wakacji ({Path.GetFileName(cacheFile)}): {e.Message}");
            return null;
        }
    }

    private void WriteCache(CacheFile cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        var temporary = cacheFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(cache, CacheJson));
        File.Move(temporary, cacheFile, overwrite: true);
    }

    private sealed class CacheFile
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public string Subdivision { get; set; } = "";
        public DateTimeOffset FetchedAtUtc { get; set; }
        public DateOnly ValidFrom { get; set; }
        public DateOnly ValidTo { get; set; }
        public List<SchoolHolidayPeriod> Periods { get; set; } = [];
    }

    private sealed class ApiHoliday
    {
        [JsonPropertyName("startDate")] public DateOnly StartDate { get; set; }
        [JsonPropertyName("endDate")] public DateOnly EndDate { get; set; }
        [JsonPropertyName("name")] public List<ApiText>? Name { get; set; }
    }

    private sealed class ApiText
    {
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
