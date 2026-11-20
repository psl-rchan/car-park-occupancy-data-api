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

    hours = OccupancyForecast.ApplyRecentSlope(recent.Points, hours);
    hours = OccupancyForecast.ApplyTransitionHold(recent.Points, hours);
    hours = OccupancyForecast.ApplyResidualFloor(recent.Points, hours);
    hours = OccupancyForecast.ApplyNoUnjustifiedFull(recent.Points, hours);

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

// Chart-aligned read: actual from row H, Forecast* from row H-1 (made at H for H+1).
app.MapGet("/api/occupancy/official-hourly", async (
    string? carParkCode,
    DateTimeOffset? from,
    DateTimeOffset? to,
    IOccupancyStore store) =>
{
    if (string.IsNullOrWhiteSpace(carParkCode) || carParkCode.Length > 50)
        return Results.BadRequest(new { error = "carParkCode is required (max 50 chars)." });
    if (from is not { } fromValue || to is not { } toValue)
        return Results.BadRequest(new { error = "from and to are required." });
    if (fromValue > toValue)
        return Results.BadRequest(new { error = "from must be before or equal to to." });

    // One hour before from so the first in-window hour can take Forecast* from H-1.
    var rows = await store.GetOfficialHourlyAsync(carParkCode, fromValue.AddHours(-1), toValue);
    var byHour = rows.ToDictionary(r => r.OfficialHourHk);
    var hours = new SortedSet<DateTimeOffset>();
    foreach (var r in rows)
    {
        if (r.OfficialHourHk >= fromValue && r.OfficialHourHk <= toValue)
            hours.Add(r.OfficialHourHk);
        if (r.ForecastLevel is not null)
        {
            var next = r.OfficialHourHk.AddHours(1);
            if (next >= fromValue && next <= toValue)
                hours.Add(next);
        }
    }

    return Results.Ok(hours.Select(h =>
    {
        byHour.TryGetValue(h, out var actual);
        byHour.TryGetValue(h.AddHours(-1), out var prev);
        return new
        {
            carParkCode,
            officialHourHk = actual?.OfficialHourHk ?? prev!.OfficialHourHk.AddHours(1),
            snapshotTime = actual?.SnapshotTime,
            currentLevel = actual?.CurrentLevel,
            capacity = actual?.Capacity,
            occupancyPercent = actual?.OccupancyPercent,
            forecastLevel = prev?.ForecastLevel,
            forecastPercent = prev?.ForecastPercent
        };
    }).ToArray());
});

// Coordinator calls at :05 Asia/Hong_Kong. OfficialHourHk is the current HK clock hour (not UTC).
app.MapPost("/api/occupancy/official-hourly", async (
    IOccupancyStore store,
    IGeminiClient gemini,
    CancellationToken cancellationToken) =>
{
    var officialHourHk = OfficialHour.CurrentHongKongHour();
    var parks = (await store.GetLatestAsync())
        .Where(r => r.CountingCategory == "Total" && r.CarparkNo == 0)
        .ToArray();

    var upserted = 0;
    var skippedMissingOfficial = 0;
    var forecastFailed = 0;
    var rows = new List<OfficialHourlyRow>(parks.Length);

    foreach (var park in parks)
    {
        var official = await ResolveOfficial00Async(store, park, officialHourHk);
        if (official is null)
        {
            skippedMissingOfficial++;
            continue;
        }

        var forecastLevel = await TryForecastPlusOneAsync(store, gemini, official, cancellationToken);
        if (forecastLevel is null)
            forecastFailed++;

        rows.Add(new OfficialHourlyRow
        {
            OfficialHourHk = officialHourHk,
            CarParkCode = official.CarParkCode,
            SnapshotTime = official.SnapshotTime,
            CurrentLevel = official.CurrentLevel,
            Capacity = official.Capacity,
            OccupancyPercent = OccupancyPct(official.CurrentLevel, official.Capacity),
            ForecastLevel = forecastLevel,
            ForecastPercent = forecastLevel is { } level ? OccupancyPct(level, official.Capacity) : null
        });
        upserted++;
    }

    if (rows.Count > 0)
        await store.UpsertOfficialHourlyAsync(rows);

    return Results.Ok(new { officialHourHk, upserted, skippedMissingOfficial, forecastFailed });
});

app.Run();

static async Task<OccupancyRow?> ResolveOfficial00Async(
    IOccupancyStore store, OccupancyRow latest, DateTimeOffset officialHourHk)
{
    if (OfficialHour.IsOfficial00(latest.SnapshotTime, officialHourHk))
        return latest;

    var history = await store.GetHistoryAsync(
        latest.CarParkCode, "Total", 0, officialHourHk, officialHourHk.AddMinutes(1));
    return history.LastOrDefault(r => OfficialHour.IsOfficial00(r.SnapshotTime, officialHourHk));
}

// Same OccupancyForecast path as GET /predict, but tip is the official :00 board only.
static async Task<int?> TryForecastPlusOneAsync(
    IOccupancyStore store, IGeminiClient gemini, OccupancyRow official, CancellationToken cancellationToken)
{
    var recent = await store.GetRecentAsync(official.CarParkCode, "Total");
    var points = recent.Points.Where(p => p.SnapshotTime <= official.SnapshotTime).ToList();
    if (points.Count == 0 || points[^1].SnapshotTime != official.SnapshotTime)
    {
        points.Add(new OccupancyPoint
        {
            SnapshotTime = official.SnapshotTime,
            CountingCategory = "Total",
            CurrentLevel = official.CurrentLevel,
            Capacity = official.Capacity
        });
    }

    try
    {
        var reply = await gemini.GenerateAsync(
            OccupancyForecast.BuildPrompt(official.CarParkCode, "Total", points), cancellationToken);
        if (!OccupancyForecast.TryParse(reply.Text, out var hours))
            return null;
        hours = OccupancyForecast.ApplyRecentSlope(points, hours);
        hours = OccupancyForecast.ApplyTransitionHold(points, hours);
        hours = OccupancyForecast.ApplyResidualFloor(points, hours);
        hours = OccupancyForecast.ApplyNoUnjustifiedFull(points, hours);
        return hours.First(h => h.HourOffset == 1).CurrentLevel;
    }
    catch (GeminiException)
    {
        return null;
    }
}

static decimal? OccupancyPct(int level, int capacity) =>
    capacity > 0 ? (decimal?)Math.Round(100.0 * level / capacity, 1) : null;

public static class OfficialHour
{
    static readonly TimeSpan HongKongOffset = TimeSpan.FromHours(8);

    public static DateTimeOffset CurrentHongKongHour()
    {
        var hk = DateTimeOffset.UtcNow.ToOffset(HongKongOffset);
        return new DateTimeOffset(hk.Year, hk.Month, hk.Day, hk.Hour, 0, 0, HongKongOffset);
    }

    public static bool IsOfficial00(DateTimeOffset snapshotTime, DateTimeOffset officialHourHk)
    {
        var hk = snapshotTime.ToOffset(HongKongOffset);
        return hk.Minute == 0
            && new DateTimeOffset(hk.Year, hk.Month, hk.Day, hk.Hour, 0, 0, HongKongOffset) == officialHourHk;
    }
}

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
    // Nullable dbo.CarPark catalog fields (INNER JOIN on latest only; closed parks omitted).
    public string? Area { get; init; }
    public string? District { get; init; }
    public string? SubDistrict { get; init; }
    public string? AreaEn { get; init; }
    public string? DistrictEn { get; init; }
    public string? SubDistrictEn { get; init; }
    public string? Name { get; init; }
    public string? NameEn { get; init; }
}

public record OfficialHourlyRow
{
    public required DateTimeOffset OfficialHourHk { get; init; }
    public required string CarParkCode { get; init; }
    public required DateTimeOffset SnapshotTime { get; init; }
    public required int CurrentLevel { get; init; }
    public required int Capacity { get; init; }
    public decimal? OccupancyPercent { get; init; }
    public int? ForecastLevel { get; init; }
    public decimal? ForecastPercent { get; init; }
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
    Task UpsertOfficialHourlyAsync(IReadOnlyCollection<OfficialHourlyRow> rows);
    Task<IReadOnlyList<OfficialHourlyRow>> GetOfficialHourlyAsync(
        string carParkCode, DateTimeOffset from, DateTimeOffset to);
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
    // Clear fill or dump. Overnight drift (about 1 pp/hour) stays under this.
    const double SteepPercentPerHour = 15;
    // Already high enough that a near-full +1 can be plausible without a steep climb.
    const double HighOccupancyPercent = 70;
    // Near-full / full forecasts need inflow evidence when below HighOccupancyPercent.
    const double NearFullPercent = 90;

    // Asia/Hong_Kong is UTC+8 year-round.
    static DateTimeOffset HongKong(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(8));

    public static (DateTimeOffset RecentSince, DateTimeOffset PriorSince, DateTimeOffset PriorUntil) SampleWindow(DateTimeOffset now)
    {
        var weekAgo = now.AddHours(-WeekHours);
        var clockUntil = weekAgo.AddHours(HorizonHours);
        var emptyingEnd = EmptyingStart(now).AddHours(9);
        var priorUntil = clockUntil > emptyingEnd ? clockUntil : emptyingEnd;
        var overnightEnd = OvernightEnd(now);
        return (now.AddHours(-RecentHours), weekAgo.AddHours(-RecentHours),
            overnightEnd > priorUntil ? overnightEnd : priorUntil);
    }

    static DateTimeOffset EmptyingStart(DateTimeOffset instant)
    {
        var hk = HongKong(instant.AddHours(-WeekHours));
        return new DateTimeOffset(hk.Year, hk.Month, hk.Day, 17, 0, 0, hk.Offset);
    }

    static DateTimeOffset OvernightEnd(DateTimeOffset instant)
    {
        var start = EmptyingStart(instant);
        return new DateTimeOffset(start.Year, start.Month, start.Day, 8, 0, 0, start.Offset).AddDays(1);
    }

    public static string BuildPrompt(string carParkCode, string category, IReadOnlyList<OccupancyPoint> points)
    {
        var history = string.Join(',', points.Select(p =>
            "{\"t\":\"" + HongKong(p.SnapshotTime).ToString("ddd yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            + "\",\"level\":" + p.CurrentLevel.ToString(CultureInfo.InvariantCulture)
            + ",\"cap\":" + p.Capacity.ToString(CultureInfo.InvariantCulture) + "}"));
        var cap = points[^1].Capacity;
        var latestInstant = points[^1].SnapshotTime;
        var latest = HongKong(latestInstant);
        var weekday = latest.ToString("dddd", CultureInfo.InvariantCulture);
        var latestText = latest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var capRule = cap > 0 ? " Do not predict a currentLevel above " + cap.ToString(CultureInfo.InvariantCulture) + "." : "";
        var slopeRule = SlopeRule(points);
        var curve = HourlyNear(points, EmptyingStart(latestInstant), 10);
        var clock = HourlyNear(points, latestInstant.AddHours(-WeekHours), HorizonHours + 1);
        var blocks = LabeledBlock(
            "Last week's same-weekday emptying curve (hourly actuals, Asia/Hong_Kong, 17:00 through 02:00 the next morning):",
            curve);
        if (curve.Count > 0)
            blocks += "\nUse this curve for emptying: when the drop starts, how fast it falls, and how much occupancy remains after it bottoms out. Do not predict 0 at an hour offset where last week still had residual cars. Do not smooth away the evening drop.";
        blocks += LabeledBlock(
            "Last week at this clock time and the next 6 hours (Asia/Hong_Kong):",
            clock);
        var shape = """{"forecasts":[{"hourOffset":1,"currentLevel":0}]}""";
        return $"""
            Forecast CurrentLevel (vehicles currently parked) for car park {carParkCode}, counting category {category}.
            Times are Asia/Hong_Kong. The latest snapshot is {weekday} {latestText}. Latest capacity is {cap}.
            History is oldest-first and skips the days in between: the same clock hours one week earlier (from {RecentHours} hours before that instant through the later of {HorizonHours} hours after it and 02:00 the next morning), then the last {RecentHours} hours at about 15-minute cadence.
            Compare the same weekday and the same clock time in that history. Follow the recent 15-45 minute climb or drop rather than smoothing away lunch and evening peaks.
            On weekday mornings about 08:00-10:00 HKT, if the recent 15-minute slope is up, hourOffset 1 must not revert toward a lower historical average or median. On weekday evenings about 17:00-19:00 HKT, if the recent slope is down, hourOffset 1 must not revert toward a higher historical average or median.
            Without a clear recent upward slope, and when the latest level is not already high or rising toward capacity, do not forecast near-full or 100% of capacity.
            {slopeRule}Return exactly {HorizonHours} hourly forecasts for hourOffset 1 through {HorizonHours}, where 1 is one hour after the latest snapshot.
            currentLevel is an integer >= 0.{capRule}
            Reply with JSON only, no markdown, in this shape: {shape} with {HorizonHours} items.
            History: [{history}]{blocks}
            """;
    }

    static List<OccupancyPoint> HourlyNear(IReadOnlyList<OccupancyPoint> points, DateTimeOffset first, int count)
    {
        var picked = new List<OccupancyPoint>(count);
        for (var h = 0; h < count; h++)
        {
            var target = first.AddHours(h);
            OccupancyPoint? best = null;
            var bestMinutes = 31.0;
            foreach (var p in points)
            {
                var minutes = Math.Abs((p.SnapshotTime - target).TotalMinutes);
                if (minutes <= 30 && minutes < bestMinutes)
                {
                    best = p;
                    bestMinutes = minutes;
                }
            }

            if (best is not null && (picked.Count == 0 || picked[^1].SnapshotTime != best.SnapshotTime))
                picked.Add(best);
        }

        return picked;
    }

    static string LabeledBlock(string title, IReadOnlyList<OccupancyPoint> rows)
    {
        if (rows.Count == 0) return "";
        return "\n" + title + "\n" + string.Join('\n', rows.Select(p =>
        {
            var t = HongKong(p.SnapshotTime);
            return t.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + " level " + p.CurrentLevel.ToString(CultureInfo.InvariantCulture)
                + " cap " + p.Capacity.ToString(CultureInfo.InvariantCulture);
        }));
    }

    // When the latest quarter-hours are already a steep climb or drop, replace the model hours
    // with that slope. A flat or gentle series is returned unchanged.
    public static IReadOnlyList<HourForecast> ApplyRecentSlope(
        IReadOnlyList<OccupancyPoint> points, IReadOnlyList<HourForecast> forecasts)
    {
        if (!TrySteepPerHour(points, out var perHour))
            return forecasts;

        var latest = points[^1];
        return forecasts
            .Select(h => new HourForecast(h.HourOffset, ProjectLevel(latest.CurrentLevel, perHour, h.HourOffset, latest.Capacity)))
            .ToArray();
    }

    // Weekday morning fill / evening outflow: keep hourOffset 1 on the recent slope
    // instead of letting the model snap back to a historical average. Night unchanged.
    public static IReadOnlyList<HourForecast> ApplyTransitionHold(
        IReadOnlyList<OccupancyPoint> points, IReadOnlyList<HourForecast> forecasts)
    {
        if (points.Count == 0) return forecasts;
        var latest = points[^1];
        var hk = HongKong(latest.SnapshotTime);
        if (hk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return forecasts;
        int needSign;
        if (hk.Hour is >= 8 and < 10) needSign = 1;
        else if (hk.Hour is >= 17 and < 19) needSign = -1;
        else return forecasts;
        if (!TrySlopePerHour(points, out var perHour) || Math.Sign(perHour) != needSign)
            return forecasts;

        var projected = ProjectLevel(latest.CurrentLevel, perHour, 1, latest.Capacity);
        return forecasts.Select(h =>
        {
            if (h.HourOffset != 1) return h;
            var level = needSign > 0
                ? Math.Max(h.CurrentLevel, projected)
                : Math.Min(h.CurrentLevel, projected);
            return new HourForecast(1, level);
        }).ToArray();
    }

    public static IReadOnlyList<HourForecast> ApplyResidualFloor(
        IReadOnlyList<OccupancyPoint> points, IReadOnlyList<HourForecast> forecasts)
    {
        if (points.Count == 0) return forecasts;
        var latest = points[^1];
        var start = EmptyingStart(latest.SnapshotTime);
        var end = OvernightEnd(latest.SnapshotTime);
        int? overnightMin = null;
        foreach (var p in points)
        {
            if (p.SnapshotTime < start || p.SnapshotTime > end) continue;
            if (overnightMin is null || p.CurrentLevel < overnightMin)
                overnightMin = p.CurrentLevel;
        }

        if (overnightMin is null) return forecasts;
        var floor = latest.CurrentLevel < overnightMin ? latest.CurrentLevel : overnightMin.Value;
        var cap = latest.Capacity;
        return forecasts.Select(h =>
        {
            var level = h.CurrentLevel < floor ? floor : h.CurrentLevel;
            if (level < 0) level = 0;
            if (cap > 0 && level > cap) level = cap;
            return new HourForecast(h.HourOffset, level);
        }).ToArray();
    }

    // Stable low parks must not jump to near-full / 100% with no upward inflow.
    public static IReadOnlyList<HourForecast> ApplyNoUnjustifiedFull(
        IReadOnlyList<OccupancyPoint> points, IReadOnlyList<HourForecast> forecasts)
    {
        if (points.Count == 0) return forecasts;
        var latest = points[^1];
        if (latest.Capacity <= 0) return forecasts;
        if (100.0 * latest.CurrentLevel / latest.Capacity >= HighOccupancyPercent)
            return forecasts;
        if (TrySlopePerHour(points, out var perHour) && perHour > 0)
            return forecasts;

        var nearFullAt = (int)Math.Ceiling(NearFullPercent / 100.0 * latest.Capacity);
        return forecasts.Select(h =>
            h.CurrentLevel >= nearFullAt
                ? new HourForecast(h.HourOffset, latest.CurrentLevel)
                : h).ToArray();
    }

    static string SlopeRule(IReadOnlyList<OccupancyPoint> points)
    {
        if (!TrySteepPerHour(points, out var perHour))
            return "";

        var latest = points[^1];
        var list = string.Join(',', Enumerable.Range(1, HorizonHours).Select(h =>
            ProjectLevel(latest.CurrentLevel, perHour, h, latest.Capacity).ToString(CultureInfo.InvariantCulture)));
        var rate = perHour.ToString("0", CultureInfo.InvariantCulture);
        return $"Hard rule: recent 15-minute slope is {rate} vehicles/hour. currentLevel for hourOffset 1 through {HorizonHours} must be {list}.\n";
    }

    // Net vehicles/hour across the latest 2–4 snapshots about 15 minutes apart.
    // A flat latest step, or a reversal in that window, is not one slope.
    static bool TrySlopePerHour(IReadOnlyList<OccupancyPoint> points, out double perHour)
    {
        perHour = 0;
        if (points.Count < 2) return false;
        var latest = points[^1];
        if (latest.Capacity <= 0) return false;

        var picked = new List<OccupancyPoint>(4);
        var cursor = latest.SnapshotTime;
        for (var i = points.Count - 1; i >= 0 && picked.Count < 4; i--)
        {
            var p = points[i];
            if ((latest.SnapshotTime - p.SnapshotTime).TotalMinutes > 50) break;
            if (picked.Count > 0 && (cursor - p.SnapshotTime).TotalMinutes < 10) continue;
            picked.Add(p);
            cursor = p.SnapshotTime;
        }

        if (picked.Count < 2) return false;
        var direction = Math.Sign(picked[0].CurrentLevel - picked[1].CurrentLevel);
        if (direction == 0) return false;
        for (var i = 1; i < picked.Count - 1; i++)
        {
            var delta = picked[i].CurrentLevel - picked[i + 1].CurrentLevel;
            if (delta != 0 && Math.Sign(delta) != direction) return false;
        }

        var hours = (picked[0].SnapshotTime - picked[^1].SnapshotTime).TotalHours;
        if (hours <= 0) return false;
        perHour = (picked[0].CurrentLevel - picked[^1].CurrentLevel) / hours;
        return true;
    }

    static bool TrySteepPerHour(IReadOnlyList<OccupancyPoint> points, out double perHour) =>
        TrySlopePerHour(points, out perHour)
        && Math.Abs(100.0 * perHour / points[^1].Capacity) >= SteepPercentPerHour;

    static int ProjectLevel(int latest, double perHour, int hourOffset, int cap)
    {
        var level = (int)Math.Round(latest + perHour * hourOffset, MidpointRounding.AwayFromZero);
        if (level < 0) level = 0;
        if (cap > 0 && level > cap) level = cap;
        return level;
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
        SELECT latest.CarParkCode, latest.SnapshotTime, latest.CarparkNo, latest.CarparkDesig,
               latest.CountingCategoryNo, latest.CountingCategory,
               latest.FreeLimit, latest.OccupancyLimit, latest.CurrentLevel, latest.Capacity,
               cp.Area, cp.District, cp.SubDistrict, cp.AreaEn, cp.DistrictEn, cp.SubDistrictEn,
               cp.Name, cp.NameEn
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (
                       PARTITION BY CarParkCode, CountingCategory
                       ORDER BY SnapshotTime DESC, Id DESC) AS rn
            FROM dbo.OccupancySnapshot
            WHERE CarparkNo = 0
        ) latest
        INNER JOIN dbo.CarPark cp ON cp.CarParkCode = latest.CarParkCode
        WHERE rn = 1
          AND cp.IsClosed = 0
        ORDER BY latest.CarParkCode, latest.CountingCategory
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

    private const string UpsertOfficialHourlySql = """
        MERGE dbo.OfficialHourlyOccupancy AS t
        USING (SELECT @OfficialHourHk AS OfficialHourHk, @CarParkCode AS CarParkCode) AS s
          ON t.OfficialHourHk = s.OfficialHourHk AND t.CarParkCode = s.CarParkCode
        WHEN MATCHED THEN UPDATE SET
            SnapshotTime = @SnapshotTime,
            CurrentLevel = @CurrentLevel,
            Capacity = @Capacity,
            OccupancyPercent = @OccupancyPercent,
            ForecastLevel = @ForecastLevel,
            ForecastPercent = @ForecastPercent
        WHEN NOT MATCHED THEN INSERT
            (OfficialHourHk, CarParkCode, SnapshotTime, CurrentLevel, Capacity,
             OccupancyPercent, ForecastLevel, ForecastPercent)
        VALUES
            (@OfficialHourHk, @CarParkCode, @SnapshotTime, @CurrentLevel, @Capacity,
             @OccupancyPercent, @ForecastLevel, @ForecastPercent);
        """;

    private const string OfficialHourlySql = """
        SELECT OfficialHourHk, CarParkCode, SnapshotTime, CurrentLevel, Capacity,
               OccupancyPercent, ForecastLevel, ForecastPercent
        FROM dbo.OfficialHourlyOccupancy
        WHERE CarParkCode = @CarParkCode
          AND OfficialHourHk >= @FromTime
          AND OfficialHourHk <= @ToTime
        ORDER BY OfficialHourHk ASC
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

    public async Task UpsertOfficialHourlyAsync(IReadOnlyCollection<OfficialHourlyRow> rows)
    {
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(UpsertOfficialHourlySql, rows, tx);
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<OfficialHourlyRow>> GetOfficialHourlyAsync(
        string carParkCode, DateTimeOffset from, DateTimeOffset to)
    {
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();
        return (await conn.QueryAsync<OfficialHourlyRow>(OfficialHourlySql, new
        {
            CarParkCode = carParkCode,
            FromTime = from,
            ToTime = to
        })).AsList();
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
