using System.Text.Json;

namespace Aptabase.Application.Ingestion;

public class TinybirdIngestionClient : IIngestionClient
{
    // Events from Debug builds are kept for 6 months
    private static readonly TimeSpan DebugTTL = TimeSpan.FromDays(182);

    // Events from Release builds are kept for 5 years
    private static readonly TimeSpan ReleaseTTL = TimeSpan.FromDays(5 * 365);

    private static readonly JsonSerializerOptions JsonSettings = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private HttpClient _httpClient;
    private ILogger _logger;

    public TinybirdIngestionClient(IHttpClientFactory factory, ILogger<TinybirdIngestionClient> logger)
    {
        _httpClient = factory.CreateClient("Tinybird");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string EventsPath => $"/v0/events?name=events";

    public async Task<InsertResult> SendSingleAsync(EventHeader header, EventBody body, CancellationToken cancellationToken)
    {
        var row = ToEventRow(header, body);

        var response = await _httpClient.PostAsJsonAsync(EventsPath, row, JsonSettings, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InsertResult>() ?? new InsertResult();
    }

    public async Task<InsertResult> SendMultipleAsync(EventHeader header, EventBody[] body, CancellationToken cancellationToken)
    {
        var rowsTask = (body ?? Enumerable.Empty<EventBody>()).Select(e =>
        {
            var row = ToEventRow(header, e);
            return JsonSerializer.Serialize(row, JsonSettings);
        });

        var content = new StringContent(string.Join('\n', rowsTask));
        var response = await _httpClient.PostAsync(EventsPath, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InsertResult>() ?? new InsertResult();
    }

    private EventRow ToEventRow(EventHeader header, EventBody body)
    {
        var ttlTimeSpan = body.SystemProps.IsDebug ? DebugTTL : ReleaseTTL;
        var appId = body.SystemProps.IsDebug ? $"{header.AppId}_DEBUG" : header.AppId;
        var (stringProps, numericProps) = body.SplitProps();
        return new EventRow
        {
            AppId = appId,
            EventName = body.EventName,
            Timestamp = body.Timestamp.ToUniversalTime().ToString("o"),
            SessionId = body.SessionId,
            OSName = body.SystemProps.OSName ?? "",
            OSVersion = body.SystemProps.OSVersion ?? "",
            Locale = FormatLocale(body.SystemProps.Locale),
            AppVersion = body.SystemProps.AppVersion ?? "",
            EngineName = body.SystemProps.EngineName ?? "",
            EngineVersion = body.SystemProps.EngineVersion ?? "",
            AppBuildNumber = body.SystemProps.AppBuildNumber ?? "",
            SdkVersion = body.SystemProps.SdkVersion ?? "",
            CountryCode = header.CountryCode ?? "",
            RegionName = header.RegionName ?? "",
            City = header.City ?? "",
            StringProps = stringProps.ToJsonString(),
            NumericProps = numericProps.ToJsonString(),
            TTL = body.Timestamp.ToUniversalTime().Add(ttlTimeSpan).ToString("o"),
        };
    }

    // List of locales that are longer than 5 characters
    // In future we might want to extend this to more locales
    private Dictionary<string, string> LongerLocales = new()
    {
        { "es-419", "es-419" },
        { "zh-hans", "zh-Hans" },
        { "zh-hans-cn", "zh-Hans-CN" },
        { "zh-hans-hk", "zh-Hans-HK" },
        { "zh-hans-mo", "zh-Hans-MO" },
        { "zh-hans-sg", "zh-Hans-SG" },
        { "zh-hant", "zh-Hant" },
        { "zh-hant-hk", "zh-Hant-HK" },
        { "zh-hant-mo", "zh-Hant-MO" },
        { "zh-hant-tw", "zh-Hant-TW" },
    };

    private string FormatLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
            return "";

        if (locale.Length != 2 && locale.Length != 5)
        {
            if (LongerLocales.TryGetValue(locale.ToLower(), out var formattedLocale))
                return formattedLocale;

            _logger.LogWarning("Invalid locale {Locale}", locale);
            return "";
        }

        var parts = locale.Replace("_", "-").Split('-');
        if (parts.Length == 1)
            return parts[0].ToLower();

        return $"{parts[0].ToLower()}-{parts[1].ToUpper()}";
    }
}