using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IOccupancyStore, SqlOccupancyStore>();
builder.Services.AddSingleton<IGeminiClient>(sp =>
    new GeminiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, sp.GetRequiredService<IConfiguration>()));
var app = builder.Build();

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
    Results.Ok(await store.GetLatestAsync()));

app.MapGet("/api/occupancy/predict", async (
    string? carParkCode,
    string? countingCategory,
    IOccupancyStore store,
    IGeminiClient gemini,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(carParkCode) || carParkCode.Length > 50)
        return Results.BadRequest(new { error = "carParkCode is required (max 50 chars)." });

    var categoryFilter = string.IsNullOrWhiteSpace(countingCategory) ? null : countingCategory.Trim();
    if (categoryFilter is { Length: > 100 })
        return Results.BadRequest(new { error = "countingCategory is too long (max 100 chars)." });

    var recent = await store.GetRecentAsync(carParkCode, categoryFilter);
    if (categoryFilter is null && recent.Categories.Count > 1)
        return Results.BadRequest(new { error = $"countingCategory is required when more than one series exists in the last {OccupancyForecast.HistoryHours} hours." });
    if (recent.Points.Count == 0)
        return Results.NotFound(new { error = $"No occupancy history in the last {OccupancyForecast.HistoryHours} hours." });

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
    public const int HistoryHours = 48;
    public const int MaxPoints = 200;
    public const int HorizonHours = 6;
    public const string DefaultModel = "gemini-3.8-flash";

    public static string BuildPrompt(string carParkCode, string category, IReadOnlyList<OccupancyPoint> points)
    {
        var history = string.Join(',', points.Select(p =>
            "{\"t\":\"" + p.SnapshotTime.ToString("O", CultureInfo.InvariantCulture)
            + "\",\"level\":" + p.CurrentLevel.ToString(CultureInfo.InvariantCulture)
            + ",\"cap\":" + p.Capacity.ToString(CultureInfo.InvariantCulture) + "}"));
        var cap = points[^1].Capacity;
        var capRule = cap > 0 ? " Do not predict a currentLevel above " + cap.ToString(CultureInfo.InvariantCulture) + "." : "";
        var shape = """{"forecasts":[{"hourOffset":1,"currentLevel":0}]}""";
        return $"""
            Forecast CurrentLevel (vehicles currently parked) for car park {carParkCode}, counting category {category}.
            History below is oldest-first, from the last {HistoryHours} hours, at most {MaxPoints} snapshots. Latest capacity is {cap}.
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
        ) latest
        WHERE rn = 1
        ORDER BY CarParkCode, CountingCategory
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

    public async Task<RecentOccupancy> GetRecentAsync(string carParkCode, string? countingCategory)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-OccupancyForecast.HistoryHours);
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();

        var categories = (await conn.QueryAsync<string>("""
            SELECT DISTINCT CountingCategory
            FROM dbo.OccupancySnapshot
            WHERE CarParkCode = @CarParkCode AND SnapshotTime >= @Since
            """, new { CarParkCode = carParkCode, Since = since })).AsList();

        if (categories.Count == 0)
            return new RecentOccupancy([], []);
        if (countingCategory is null && categories.Count > 1)
            return new RecentOccupancy(categories, []);

        var category = countingCategory ?? categories[0];
        var points = (await conn.QueryAsync<OccupancyPoint>("""
            SELECT TOP (@Take) SnapshotTime, CountingCategory, CurrentLevel, Capacity
            FROM dbo.OccupancySnapshot
            WHERE CarParkCode = @CarParkCode
              AND CountingCategory = @CountingCategory
              AND SnapshotTime >= @Since
            ORDER BY SnapshotTime DESC
            """, new
        {
            CarParkCode = carParkCode,
            CountingCategory = category,
            Since = since,
            Take = OccupancyForecast.MaxPoints
        })).AsList();

        points.Reverse();
        return new RecentOccupancy(points.Count == 0 ? [] : [category], points);
    }
}

/// <summary>
/// Gemini API (AI Studio) generateContent call. Auth is an API key header, not ADC.
/// POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
/// </summary>
public sealed class GeminiClient(HttpClient http, IConfiguration config) : IGeminiClient
{
    static readonly Regex ModelId = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<GeminiReply> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var key = FirstNonEmpty(config["GEMINI_API_KEY"], config["Vertex:ApiKey"]);
        if (key is null)
            throw new GeminiException(StatusCodes.Status503ServiceUnavailable, "Gemini API key is not configured. Set GEMINI_API_KEY or Vertex__ApiKey.");

        var model = FirstNonEmpty(config["Vertex:Model"]) ?? OccupancyForecast.DefaultModel;
        if (!ModelId.IsMatch(model))
            throw new GeminiException(StatusCodes.Status503ServiceUnavailable, "Vertex__Model must be a Gemini model id such as gemini-3.8-flash.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
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
