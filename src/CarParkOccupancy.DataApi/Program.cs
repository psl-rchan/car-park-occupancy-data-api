using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IOccupancyStore, SqlOccupancyStore>();
builder.Services.AddSingleton<IGeminiClient>(sp =>
    new GeminiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, sp.GetRequiredService<IConfiguration>()));
var app = builder.Build();

// /api/occupancy requires header X-Api-Key (config/env ApiKey). /health stays open.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/occupancy") && !HasValidApiKey(ctx))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
        return;
    }

    await next(ctx);
});

static bool HasValidApiKey(HttpContext ctx)
{
    var expected = ctx.RequestServices.GetRequiredService<IConfiguration>()["ApiKey"];
    if (string.IsNullOrWhiteSpace(expected))
        return false;

    var provided = ctx.Request.Headers["X-Api-Key"].ToString();
    var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
    return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/occupancy", async (OccupancyRow[]? rows, IOccupancyStore store) =>
{
    if (rows is null || rows.Length == 0)
        return Results.BadRequest(new { error = "Body must be a non-empty JSON array." });

    for (var i = 0; i < rows.Length; i++)
    {
        var r = rows[i];
        if (string.IsNullOrWhiteSpace(r.CarParkCode) || r.CarParkCode.Length > 50)
            return Results.BadRequest(new { error = $"rows[{i}].CarParkCode is required (max 50 chars)." });
        if (string.IsNullOrWhiteSpace(r.CarparkDesig) || r.CarparkDesig.Length > 100)
            return Results.BadRequest(new { error = $"rows[{i}].CarparkDesig is required (max 100 chars)." });
        if (string.IsNullOrWhiteSpace(r.CountingCategory) || r.CountingCategory.Length > 100)
            return Results.BadRequest(new { error = $"rows[{i}].CountingCategory is required (max 100 chars)." });
    }

    await store.InsertAsync(rows);
    return Results.Ok(new { inserted = rows.Length });
});

app.MapGet("/api/occupancy/latest", async (IOccupancyStore store) =>
{
    var rows = await store.GetLatestAsync();
    return Results.Ok(rows.Where(r => r.CountingCategory == "Total" && r.CarparkNo == 0).ToArray());
});

app.MapGet("/api/occupancy/history", async (
    string? carParkCode,
    string? countingCategory,
    int? carparkNo,
    double? hoursBack,
    DateTimeOffset? from,
    DateTimeOffset? to,
    IOccupancyStore store) =>
{
    if (string.IsNullOrWhiteSpace(carParkCode) || carParkCode.Length > 50)
        return Results.BadRequest(new { error = "carParkCode is required (max 50 chars)." });

    var category = string.IsNullOrWhiteSpace(countingCategory) ? "Total" : countingCategory.Trim();
    if (category != "Total")
        return Results.BadRequest(new { error = "countingCategory must be Total." });

    if (hoursBack is not null && (from is not null || to is not null))
        return Results.BadRequest(new { error = "Pass hoursBack or from and to, not both." });

    DateTimeOffset fromTime, toTime;
    if (hoursBack is { } hours)
    {
        if (!(hours > 0))
            return Results.BadRequest(new { error = "hoursBack must be greater than 0." });
        toTime = DateTimeOffset.UtcNow;
        try
        {
            fromTime = toTime.AddHours(-hours);
        }
        catch (OverflowException)
        {
            return Results.BadRequest(new { error = "hoursBack is out of range." });
        }
    }
    else if (from is { } fromValue && to is { } toValue)
    {
        if (fromValue > toValue)
            return Results.BadRequest(new { error = "from must be before or equal to to." });
        fromTime = fromValue;
        toTime = toValue;
    }
    else
        return Results.BadRequest(new { error = "hoursBack or both from and to are required." });

    return Results.Ok(await store.GetHistoryAsync(carParkCode, category, carparkNo ?? 0, fromTime, toTime));
});

app.MapGet("/api/occupancy/predict", async (
    string? carParkCode,
    string? countingCategory,
    IOccupancyStore store,
    IGeminiClient gemini,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(carParkCode) || carParkCode.Length > 50)
        return Results.BadRequest(new { error = "carParkCode is required (max 50 chars)." });

    var categoryFilter = string.IsNullOrWhiteSpace(countingCategory) ? "Total" : countingCategory.Trim();
    if (categoryFilter.Length > 100)
        return Results.BadRequest(new { error = "countingCategory is too long (max 100 chars)." });
    if (categoryFilter != "Total")
        return Results.BadRequest(new { error = "countingCategory must be Total." });

    var recent = await store.GetRecentAsync(carParkCode, categoryFilter);
    if (recent.Points.Count == 0)
        return Results.NotFound(new { error = $"No occupancy history in the last {OccupancyForecast.RecentHours} hours." });

    var usedCategory = recent.Points[^1].CountingCategory;
    var capacity = recent.Points[^1].Capacity;

    GeminiReply reply;
    try
    {
        reply = await gemini.GenerateAsync(OccupancyForecast.BuildPrompt(carParkCode, usedCategory, recent.Points), cancellationToken);
    }
    catch (GeminiException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }

    if (!OccupancyForecast.TryParse(reply.Text, out var hours))
        return Results.Json(new { error = "Gemini returned an unreadable forecast." }, statusCode: StatusCodes.Status502BadGateway);

    return Results.Ok(new
    {
        carParkCode,
        countingCategory = usedCategory,
        model = reply.Model,
        forecasts = hours.Select(h => new
        {
            hourOffset = h.HourOffset,
            currentLevel = h.CurrentLevel,
            occupancyPercent = capacity > 0 ? Math.Round(100.0 * h.CurrentLevel / capacity, 1) : (double?)null
        })
    });
});

app.Run();

public record OccupancyRow
{
    public required string CarParkCode { get; init; }
    public required DateTimeOffset SnapshotTime { get; init; }
    public required int CarparkNo { get; init; }
    public required string CarparkDesig { get; init; }
    public required int CountingCategoryNo { get; init; }
    public required string CountingCategory { get; init; }
    public required int FreeLimit { get; init; }
    public required int OccupancyLimit { get; init; }
    public required int CurrentLevel { get; init; }
    public required int Capacity { get; init; }
}

public sealed class OccupancyPoint
{
    public DateTimeOffset SnapshotTime { get; set; }
    public string CountingCategory { get; set; } = "";
    public int CurrentLevel { get; set; }
    public int Capacity { get; set; }
}

public sealed record RecentOccupancy(IReadOnlyList<string> Categories, IReadOnlyList<OccupancyPoint> Points);

public interface IOccupancyStore
{
    Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows);
    Task<IReadOnlyList<OccupancyRow>> GetLatestAsync();
    Task<IReadOnlyList<OccupancyRow>> GetHistoryAsync(
        string carParkCode, string countingCategory, int carparkNo, DateTimeOffset from, DateTimeOffset to);
    Task<RecentOccupancy> GetRecentAsync(string carParkCode, string? countingCategory);
}

public sealed record GeminiReply(string Model, string Text);

public interface IGeminiClient
{
    Task<GeminiReply> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

public sealed class GeminiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public readonly record struct HourForecast(int HourOffset, int CurrentLevel);

public static class OccupancyForecast
{
    public const int RecentHours = 48;
    public const int WeekHours = 168;
    public const int HorizonHours = 6;
    public const string DefaultModel = "gemini-3.5-flash-lite";
    // Four 15-minute slots per hour in the longer block, plus a few boundary rows.
    public const int WindowPoints = (RecentHours + HorizonHours) * 4 + 8;

    // Asia/Hong_Kong is UTC+8 year-round.
    static DateTimeOffset HongKong(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(8));

    public static (DateTimeOffset RecentSince, DateTimeOffset PriorSince, DateTimeOffset PriorUntil) SampleWindow(DateTimeOffset now)
    {
        var weekAgo = now.AddHours(-WeekHours);
        return (now.AddHours(-RecentHours), weekAgo.AddHours(-RecentHours), weekAgo.AddHours(HorizonHours));
    }

    public static string BuildPrompt(string carParkCode, string category, IReadOnlyList<OccupancyPoint> points)
    {
        var history = string.Join(',', points.Select(p =>
            "{\"t\":\"" + HongKong(p.SnapshotTime).ToString("ddd yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            + "\",\"level\":" + p.CurrentLevel.ToString(CultureInfo.InvariantCulture)
            + ",\"cap\":" + p.Capacity.ToString(CultureInfo.InvariantCulture) + "}"));
        var cap = points[^1].Capacity;
        var latest = HongKong(points[^1].SnapshotTime);
        var weekday = latest.ToString("dddd", CultureInfo.InvariantCulture);
        var latestText = latest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var capRule = cap > 0 ? " Do not predict a currentLevel above " + cap.ToString(CultureInfo.InvariantCulture) + "." : "";
        var shape = """{"forecasts":[{"hourOffset":1,"currentLevel":0}]}""";
        return $"""
            Forecast CurrentLevel (vehicles currently parked) for car park {carParkCode}, counting category {category}.
            Times are Asia/Hong_Kong. The latest snapshot is {weekday} {latestText}. Latest capacity is {cap}.
            History is oldest-first and skips the days in between: the same clock hours one week earlier ({RecentHours} hours before that instant through {HorizonHours} hours after it), then the last {RecentHours} hours at about 15-minute cadence.
            Compare the same weekday and the same clock time in that history. Follow the recent 15-45 minute climb or drop rather than smoothing away lunch and evening peaks.
            Return exactly {HorizonHours} hourly forecasts for hourOffset 1 through {HorizonHours}, where 1 is one hour after the latest snapshot.
            currentLevel is an integer >= 0.{capRule}
            Reply with JSON only, no markdown, in this shape: {shape} with {HorizonHours} items.
            History: [{history}]
            """;
    }

    public static bool TryParse(string text, out IReadOnlyList<HourForecast> forecasts)
    {
        forecasts = [];
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(text));
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arr = doc.RootElement;
            else if (!TryProperty(doc.RootElement, "forecasts", out arr) || arr.ValueKind != JsonValueKind.Array)
                return false;

            var map = new Dictionary<int, int>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                if (!TryInt(item, "hourOffset", out var hour) || !TryInt(item, "currentLevel", out var level))
                    return false;
                if (hour is < 1 or > HorizonHours || level < 0 || !map.TryAdd(hour, level))
                    return false;
            }

            if (map.Count != HorizonHours) return false;
            forecasts = Enumerable.Range(1, HorizonHours).Select(h => new HourForecast(h, map[h])).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static string ExtractJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl >= 0) t = t[(nl + 1)..];
            if (t.EndsWith("```", StringComparison.Ordinal)) t = t[..^3];
            t = t.Trim();
        }

        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start >= 0 && end > start) return t[start..(end + 1)];
        start = t.IndexOf('[');
        end = t.LastIndexOf(']');
        if (start >= 0 && end > start) return t[start..(end + 1)];
        return t;
    }

    static bool TryProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!TryProperty(obj, name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        return el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

public class SqlOccupancyStore(IConfiguration config) : IOccupancyStore
{
    private const string InsertSql = """
        INSERT INTO dbo.OccupancySnapshot
            (CarParkCode, SnapshotTime, CarparkNo, CarparkDesig, CountingCategoryNo, CountingCategory, FreeLimit, OccupancyLimit, CurrentLevel, Capacity)
        VALUES
            (@CarParkCode, @SnapshotTime, @CarparkNo, @CarparkDesig, @CountingCategoryNo, @CountingCategory, @FreeLimit, @OccupancyLimit, @CurrentLevel, @Capacity)
        """;

    private const string LatestSql = """
        SELECT CarParkCode, SnapshotTime, CarparkNo, CarparkDesig, CountingCategoryNo, CountingCategory,
               FreeLimit, OccupancyLimit, CurrentLevel, Capacity
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (
                       PARTITION BY CarParkCode, CountingCategory
                       ORDER BY SnapshotTime DESC, Id DESC) AS rn
            FROM dbo.OccupancySnapshot
            WHERE CarparkNo = 0
        ) latest
        WHERE rn = 1
        ORDER BY CarParkCode, CountingCategory
        """;

    private const string HistorySql = """
        SELECT CarParkCode, SnapshotTime, CarparkNo, CarparkDesig, CountingCategoryNo, CountingCategory,
               FreeLimit, OccupancyLimit, CurrentLevel, Capacity
        FROM dbo.OccupancySnapshot
        WHERE CarParkCode = @CarParkCode
          AND CountingCategory = @CountingCategory
          AND CarparkNo = @CarparkNo
          AND SnapshotTime >= @FromTime
          AND SnapshotTime <= @ToTime
        ORDER BY SnapshotTime ASC
        """;

    public async Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows)
    {
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(InsertSql, rows, tx);
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<OccupancyRow>> GetLatestAsync()
    {
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();
        return (await conn.QueryAsync<OccupancyRow>(LatestSql)).AsList();
    }

    public async Task<IReadOnlyList<OccupancyRow>> GetHistoryAsync(
        string carParkCode, string countingCategory, int carparkNo, DateTimeOffset from, DateTimeOffset to)
    {
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();
        return (await conn.QueryAsync<OccupancyRow>(HistorySql, new
        {
            CarParkCode = carParkCode,
            CountingCategory = countingCategory,
            CarparkNo = carparkNo,
            FromTime = from,
            ToTime = to
        })).AsList();
    }

    public async Task<RecentOccupancy> GetRecentAsync(string carParkCode, string? countingCategory)
    {
        var (recentSince, priorSince, priorUntil) = OccupancyForecast.SampleWindow(DateTimeOffset.UtcNow);
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();

        var categories = (await conn.QueryAsync<string>("""
            SELECT DISTINCT CountingCategory
            FROM dbo.OccupancySnapshot
            WHERE CarParkCode = @CarParkCode
              AND CarparkNo = 0
              AND (
                    SnapshotTime >= @RecentSince
                    OR (SnapshotTime >= @PriorSince AND SnapshotTime <= @PriorUntil)
                  )
            """, new { CarParkCode = carParkCode, RecentSince = recentSince, PriorSince = priorSince, PriorUntil = priorUntil })).AsList();

        if (categories.Count == 0)
            return new RecentOccupancy([], []);
        if (countingCategory is null && categories.Count > 1)
            return new RecentOccupancy(categories, []);

        var category = countingCategory ?? categories[0];
        var points = (await conn.QueryAsync<OccupancyPoint>("""
            SELECT SnapshotTime, CountingCategory, CurrentLevel, Capacity
            FROM (
                SELECT TOP (@Take) SnapshotTime, CountingCategory, CurrentLevel, Capacity
                FROM dbo.OccupancySnapshot
                WHERE CarParkCode = @CarParkCode
                  AND CountingCategory = @CountingCategory
                  AND CarparkNo = 0
                  AND SnapshotTime >= @RecentSince
                ORDER BY SnapshotTime DESC
            ) recent
            UNION ALL
            SELECT SnapshotTime, CountingCategory, CurrentLevel, Capacity
            FROM (
                SELECT TOP (@Take) SnapshotTime, CountingCategory, CurrentLevel, Capacity
                FROM dbo.OccupancySnapshot
                WHERE CarParkCode = @CarParkCode
                  AND CountingCategory = @CountingCategory
                  AND CarparkNo = 0
                  AND SnapshotTime >= @PriorSince
                  AND SnapshotTime <= @PriorUntil
                ORDER BY SnapshotTime DESC
            ) prior
            ORDER BY SnapshotTime ASC
            """, new
        {
            CarParkCode = carParkCode,
            CountingCategory = category,
            RecentSince = recentSince,
            PriorSince = priorSince,
            PriorUntil = priorUntil,
            Take = OccupancyForecast.WindowPoints
        })).AsList();

        if (points.Count == 0 || points[^1].SnapshotTime < recentSince)
            return new RecentOccupancy([], []);
        return new RecentOccupancy([category], points);
    }
}

/// <summary>
/// Vertex AI publisher generateContent. Auth is an API key query parameter, not ADC.
/// POST https://aiplatform.googleapis.com/v1/publishers/google/models/{model}:generateContent?key={api_key}
/// </summary>
public sealed class GeminiClient(HttpClient http, IConfiguration config) : IGeminiClient
{
    static readonly Regex ModelId = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<GeminiReply> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        // Gemini__ApiKey is primary. GEMINI_API_KEY and Vertex__ApiKey remain so an existing deploy keeps working.
        var key = FirstNonEmpty(config["Gemini:ApiKey"], config["GEMINI_API_KEY"], config["Vertex:ApiKey"]);
        if (key is null)
            throw new GeminiException(StatusCodes.Status503ServiceUnavailable, "Gemini API key is not configured. Set Gemini__ApiKey (or GEMINI_API_KEY or Vertex__ApiKey).");

        var model = FirstNonEmpty(config["Vertex:Model"]) ?? OccupancyForecast.DefaultModel;
        if (!ModelId.IsMatch(model))
            throw new GeminiException(StatusCodes.Status503ServiceUnavailable, "Vertex__Model must be a Gemini model id such as gemini-2.5-flash-lite.");

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://aiplatform.googleapis.com/v1/publishers/google/models/{model}:generateContent?key={Uri.EscapeDataString(key)}");
        req.Content = JsonContent.Create(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0, responseMimeType = "application/json" }
        });

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new GeminiException(StatusCodes.Status502BadGateway, "Gemini request failed.");
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
                throw new GeminiException(StatusCodes.Status502BadGateway, $"Gemini returned HTTP {(int)res.StatusCode}.");

            string text;
            try
            {
                text = ReadModelText(body);
            }
            catch (JsonException)
            {
                throw new GeminiException(StatusCodes.Status502BadGateway, "Gemini returned an unreadable response.");
            }

            if (string.IsNullOrWhiteSpace(text))
                throw new GeminiException(StatusCodes.Status502BadGateway, "Gemini returned an empty forecast.");
            return new GeminiReply(model, text);
        }
    }

    static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => string.IsNullOrWhiteSpace(v) ? null : v.Trim()).FirstOrDefault(v => v is not null);

    static string ReadModelText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates))
            return "";

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                continue;

            string? text = null;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } s)
                    text = s;
            }
            if (text is not null) return text;
        }

        return "";
    }
}

public partial class Program;
