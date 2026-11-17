using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class ApiTests
{
    sealed class FakeStore : IOccupancyStore
    {
        public List<OccupancyRow> Rows = [];
        // Mirrors INNER JOIN dbo.CarPark + cp.IsClosed = 0: code present and false → open catalog match.
        public Dictionary<string, bool> CatalogIsClosed = new(StringComparer.Ordinal);
        public Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows) { Rows.AddRange(rows); return Task.CompletedTask; }

        public Task<IReadOnlyList<OccupancyRow>> GetLatestAsync()
        {
            IReadOnlyList<OccupancyRow> latest = Rows
                .Where(r => r.CarparkNo == 0
                    && CatalogIsClosed.TryGetValue(r.CarParkCode, out var closed) && !closed)
                .Select((row, index) => (row, index))
                .GroupBy(x => (x.row.CarParkCode, x.row.CountingCategory))
                .Select(g => g.OrderByDescending(x => x.row.SnapshotTime).ThenByDescending(x => x.index).First().row)
                .OrderBy(r => r.CarParkCode, StringComparer.Ordinal)
                .ThenBy(r => r.CountingCategory, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(latest);
        }

        public Task<IReadOnlyList<OccupancyRow>> GetHistoryAsync(
            string carParkCode, string countingCategory, int carparkNo, DateTimeOffset from, DateTimeOffset to)
        {
            IReadOnlyList<OccupancyRow> rows = Rows
                .Where(r => r.CarParkCode == carParkCode
                    && r.CountingCategory == countingCategory
                    && r.CarparkNo == carparkNo
                    && r.SnapshotTime >= from
                    && r.SnapshotTime <= to)
                .OrderBy(r => r.SnapshotTime)
                .ToList();
            return Task.FromResult(rows);
        }

        public Task<RecentOccupancy> GetRecentAsync(string carParkCode, string? countingCategory)
        {
            var (recentSince, priorSince, priorUntil) = OccupancyForecast.SampleWindow(DateTimeOffset.UtcNow);
            var rows = Rows
                .Where(r => r.CarParkCode == carParkCode && r.CarparkNo == 0
                    && (r.SnapshotTime >= recentSince || (r.SnapshotTime >= priorSince && r.SnapshotTime <= priorUntil)))
                .ToList();

            var categories = rows.Select(r => r.CountingCategory).Distinct().ToList();
            if (categories.Count == 0)
                return Task.FromResult(new RecentOccupancy([], []));
            if (countingCategory is null && categories.Count > 1)
                return Task.FromResult(new RecentOccupancy(categories, []));

            var category = countingCategory ?? categories[0];
            static OccupancyPoint ToPoint(OccupancyRow r) => new()
            {
                SnapshotTime = r.SnapshotTime,
                CountingCategory = r.CountingCategory,
                CurrentLevel = r.CurrentLevel,
                Capacity = r.Capacity
            };

            var points = rows
                .Where(r => r.CountingCategory == category && r.SnapshotTime >= priorSince && r.SnapshotTime <= priorUntil)
                .OrderByDescending(r => r.SnapshotTime)
                .Take(OccupancyForecast.WindowPoints)
                .Select(ToPoint)
                .Concat(rows
                    .Where(r => r.CountingCategory == category && r.SnapshotTime >= recentSince)
                    .OrderByDescending(r => r.SnapshotTime)
                    .Take(OccupancyForecast.WindowPoints)
                    .Select(ToPoint))
                .OrderBy(p => p.SnapshotTime)
                .ToList();
            if (points.Count == 0 || points[^1].SnapshotTime < recentSince)
                return Task.FromResult(new RecentOccupancy([], []));
            return Task.FromResult(new RecentOccupancy([category], points));
        }
    }

    sealed class FakeGemini : IGeminiClient
    {
        public string Text = SixHours;
        public Exception? Error;
        public string? LastPrompt;

        public Task<GeminiReply> GenerateAsync(string prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return Error is null
                ? Task.FromResult(new GeminiReply("fake-model", Text))
                : Task.FromException<GeminiReply>(Error);
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    const string SixHours = """
        {"forecasts":[
          {"hourOffset":1,"currentLevel":41},
          {"hourOffset":2,"currentLevel":42},
          {"hourOffset":3,"currentLevel":43},
          {"hourOffset":4,"currentLevel":44},
          {"hourOffset":5,"currentLevel":45},
          {"hourOffset":6,"currentLevel":46}
        ]}
        """;

    const string TestApiKey = "test-api-key";
    const string Row = """{"CarParkCode":"{0}","SnapshotTime":"2026-01-01T10:00:00+08:00","CarparkNo":1,"CarparkDesig":"A","CountingCategoryNo":1,"CountingCategory":"Total","FreeLimit":5,"OccupancyLimit":95,"CurrentLevel":40,"Capacity":100}""";

    static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    static OccupancyRow Point(string code, string category, int level, int capacity = 100, double hoursAgo = 1, int carparkNo = 0) => new()
    {
        CarParkCode = code,
        SnapshotTime = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
        CarparkNo = carparkNo,
        CarparkDesig = "A",
        CountingCategoryNo = 1,
        CountingCategory = category,
        FreeLimit = 5,
        OccupancyLimit = 95,
        CurrentLevel = level,
        Capacity = capacity
    };

    static (HttpClient Client, FakeStore Store, FakeGemini Gemini) Create(bool sendKey = true, string? apiKey = TestApiKey)
    {
        var store = new FakeStore();
        var gemini = new FakeGemini();
        var client = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["ApiKey"] = apiKey });
                });
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IOccupancyStore>(store);
                    s.AddSingleton<IGeminiClient>(gemini);
                });
            })
            .CreateClient();
        if (sendKey)
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
        return (client, store, gemini);
    }

    static async Task<JsonElement> Body(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    static int[] ForecastLevels(JsonElement json)
    {
        var forecasts = json.GetProperty("forecasts");
        return Enumerable.Range(0, forecasts.GetArrayLength())
            .Select(i => forecasts[i].GetProperty("currentLevel").GetInt32())
            .ToArray();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var (c, _, _) = Create();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Occupancy_MissingOrWrongApiKey_Returns401_HealthStaysOpen()
    {
        var (missing, store, gemini) = Create(sendKey: false);
        Assert.Equal(HttpStatusCode.OK, (await missing.GetAsync("/health")).StatusCode);

        var post = await missing.PostAsync("/api/occupancy", Json($"[{Row.Replace("{0}", "CP1")}]"));
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal("Unauthorized.", (await Body(post)).GetProperty("error").GetString());
        Assert.Empty(store.Rows);
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync("/api/occupancy/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync("/api/occupancy/history?carParkCode=CP1&hoursBack=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync("/api/occupancy/predict?carParkCode=CP1")).StatusCode);
        Assert.Null(gemini.LastPrompt);

        var (wrong, _, _) = Create(sendKey: false);
        wrong.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/occupancy/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/occupancy/history?carParkCode=CP1&hoursBack=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsync("/api/occupancy", Json($"[{Row.Replace("{0}", "CP1")}]"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/occupancy/predict?carParkCode=CP1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await wrong.GetAsync("/health")).StatusCode);

        var (unset, _, _) = Create(sendKey: true, apiKey: " ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await unset.GetAsync("/api/occupancy/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await unset.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Ingest_Valid_Persists()
    {
        var (c, store, _) = Create();
        var res = await c.PostAsync("/api/occupancy", Json($"[{Row.Replace("{0}", "CP1")}]"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(store.Rows);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"SnapshotTime":"2026-01-01T10:00:00+08:00"}]""")]
    public async Task Ingest_Invalid_BadRequest(string body)
    {
        var (c, store, _) = Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/occupancy", Json(body))).StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Ingest_BlankCarParkCode_BadRequest()
    {
        var (c, _, _) = Create();
        var res = await c.PostAsync("/api/occupancy", Json($"[{Row.Replace("{0}", " ")}]"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Latest_ReturnsNewestTotalRowPerCarPark()
    {
        var (c, store, _) = Create();
        store.CatalogIsClosed["CP1"] = false;
        store.CatalogIsClosed["CP2"] = false;
        store.CatalogIsClosed["CP3"] = false;
        store.Rows.Add(Point("CP1", "Total", 10, hoursAgo: 3));
        store.Rows.Add(Point("CP1", "Total", 40, capacity: 82, hoursAgo: 1));
        store.Rows.Add(Point("CP1", "Total", 99, hoursAgo: 0.1, carparkNo: 1));
        store.Rows.Add(Point("CP2", "Total", 3, hoursAgo: 2));
        store.Rows.Add(Point("CP1", "EV", 7, hoursAgo: 2));
        store.Rows.Add(Point("CP3", "total", 9, hoursAgo: 1));
        store.Rows.Add(Point("CP4", "Total", 8, carparkNo: 2));

        var res = await c.GetAsync("/api/occupancy/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal(2, json.GetArrayLength());

        Assert.Equal("CP1", json[0].GetProperty("carParkCode").GetString());
        Assert.Equal("Total", json[0].GetProperty("countingCategory").GetString());
        Assert.Equal(0, json[0].GetProperty("carparkNo").GetInt32());
        Assert.Equal(40, json[0].GetProperty("currentLevel").GetInt32());
        Assert.Equal(82, json[0].GetProperty("capacity").GetInt32());

        Assert.Equal("CP2", json[1].GetProperty("carParkCode").GetString());
        Assert.Equal("Total", json[1].GetProperty("countingCategory").GetString());
        Assert.Equal(3, json[1].GetProperty("currentLevel").GetInt32());
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("area").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("district").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("subDistrict").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("areaEn").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("districtEn").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("subDistrictEn").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("nameEn").ValueKind);
    }

    [Fact]
    public async Task Latest_IncludesOpenCatalogFields_AndOmitsUncataloguedOrClosed()
    {
        var (c, store, _) = Create();
        store.CatalogIsClosed["CP1"] = false;
        store.CatalogIsClosed["CP3"] = false;
        store.CatalogIsClosed["CP5"] = true;
        store.Rows.Add(Point("CP1", "Total", 40, capacity: 82) with
        {
            Area = "香港島",
            District = "中西區",
            SubDistrict = "中環",
            AreaEn = "Hong Kong Island",
            DistrictEn = "Central and Western",
            SubDistrictEn = "Central",
            Name = "中區政府合署停車場",
            NameEn = "Central Government Offices Car Park"
        });
        store.Rows.Add(Point("CP2", "Total", 3)); // no catalog row → omitted
        store.Rows.Add(Point("CP3", "Total", 9) with
        {
            Area = null,
            District = null,
            SubDistrict = null,
            AreaEn = null,
            DistrictEn = null,
            SubDistrictEn = null,
            Name = null,
            NameEn = null
        });
        store.Rows.Add(Point("CP5", "Total", 12) with { Name = "closed-park" }); // IsClosed = 1 → omitted
        store.Rows.Add(Point("CP1", "EV", 7) with { Area = "should-not-appear", Name = "ev" });
        store.Rows.Add(Point("CP4", "Total", 8, carparkNo: 2) with { Name = "level-row" });

        var res = await c.GetAsync("/api/occupancy/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal(2, json.GetArrayLength());

        Assert.Equal("CP1", json[0].GetProperty("carParkCode").GetString());
        Assert.Equal(40, json[0].GetProperty("currentLevel").GetInt32());
        Assert.Equal("香港島", json[0].GetProperty("area").GetString());
        Assert.Equal("中西區", json[0].GetProperty("district").GetString());
        Assert.Equal("中環", json[0].GetProperty("subDistrict").GetString());
        Assert.Equal("Hong Kong Island", json[0].GetProperty("areaEn").GetString());
        Assert.Equal("Central and Western", json[0].GetProperty("districtEn").GetString());
        Assert.Equal("Central", json[0].GetProperty("subDistrictEn").GetString());
        Assert.Equal("中區政府合署停車場", json[0].GetProperty("name").GetString());
        Assert.Equal("Central Government Offices Car Park", json[0].GetProperty("nameEn").GetString());
        Assert.False(json[0].TryGetProperty("isClosed", out _));

        Assert.Equal("CP3", json[1].GetProperty("carParkCode").GetString());
        Assert.Equal(9, json[1].GetProperty("currentLevel").GetInt32());
        Assert.Equal(JsonValueKind.Null, json[1].GetProperty("area").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[1].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[1].GetProperty("nameEn").ValueKind);
    }

    [Fact]
    public async Task History_HoursBack_ReturnsTotalRowsInWindowOldestFirst()
    {
        var (c, store, _) = Create();
        var matchOld = Point("CP1", "Total", 10, capacity: 82, hoursAgo: 0.6) with { CarparkNo = 0 };
        var matchNew = Point("CP1", "Total", 40, capacity: 82, hoursAgo: 0.2) with { CarparkNo = 0 };
        store.Rows.Add(matchNew);
        store.Rows.Add(matchOld);
        store.Rows.Add(Point("CP1", "Total", 99, hoursAgo: 0.2));
        store.Rows.Add(Point("CP1", "EV", 7, hoursAgo: 0.2) with { CarparkNo = 0 });
        store.Rows.Add(Point("CP2", "Total", 3, hoursAgo: 0.2) with { CarparkNo = 0 });
        store.Rows.Add(Point("CP1", "Total", 1, hoursAgo: 3) with { CarparkNo = 0 });

        var res = await c.GetAsync("/api/occupancy/history?carParkCode=CP1&hoursBack=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        // Point("CP1","Total",99,hoursAgo:0.2) defaults CarparkNo=0, so it is in the window (3 rows, not 2).
        Assert.Equal(3, json.GetArrayLength());

        Assert.Equal("CP1", json[0].GetProperty("carParkCode").GetString());
        Assert.Equal(10, json[0].GetProperty("currentLevel").GetInt32());
        Assert.Equal(0, json[0].GetProperty("carparkNo").GetInt32());
        Assert.Equal("Total", json[0].GetProperty("countingCategory").GetString());
        Assert.Equal(82, json[0].GetProperty("capacity").GetInt32());
        Assert.Equal("A", json[0].GetProperty("carparkDesig").GetString());
        Assert.Equal(1, json[0].GetProperty("countingCategoryNo").GetInt32());
        Assert.Equal(5, json[0].GetProperty("freeLimit").GetInt32());
        Assert.Equal(95, json[0].GetProperty("occupancyLimit").GetInt32());
        Assert.True(json[0].GetProperty("snapshotTime").GetDateTimeOffset() < json[1].GetProperty("snapshotTime").GetDateTimeOffset());
        Assert.Equal(40, json[1].GetProperty("currentLevel").GetInt32());
        Assert.Equal(99, json[2].GetProperty("currentLevel").GetInt32());
        Assert.True(json[1].GetProperty("snapshotTime").GetDateTimeOffset() <= json[2].GetProperty("snapshotTime").GetDateTimeOffset());

        var empty = await c.GetAsync("/api/occupancy/history?carParkCode=NONE&countingCategory=Total&hoursBack=1");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, (await Body(empty)).GetArrayLength());
    }

    [Fact]
    public async Task History_FromTo_IsInclusive()
    {
        var (c, store, _) = Create();
        var from = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        store.Rows.Add(Point("CP1", "Total", 1) with { CarparkNo = 2, SnapshotTime = from.AddMinutes(-1) });
        store.Rows.Add(Point("CP1", "Total", 2) with { CarparkNo = 2, SnapshotTime = from });
        store.Rows.Add(Point("CP1", "Total", 3) with { CarparkNo = 2, SnapshotTime = to });
        store.Rows.Add(Point("CP1", "Total", 4) with { CarparkNo = 2, SnapshotTime = to.AddMinutes(1) });

        var res = await c.GetAsync("/api/occupancy/history?carParkCode=CP1&carparkNo=2&from=2026-01-01T02:00:00Z&to=2026-01-01T03:00:00Z");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal(2, json[0].GetProperty("currentLevel").GetInt32());
        Assert.Equal(3, json[1].GetProperty("currentLevel").GetInt32());
    }

    [Fact]
    public async Task History_NonTotalCategory_BadRequest()
    {
        var (c, _, _) = Create();
        var res = await c.GetAsync("/api/occupancy/history?carParkCode=CP1&countingCategory=EV&hoursBack=1");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("countingCategory must be Total.", (await Body(res)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("?hoursBack=1")]
    [InlineData("?carParkCode=%20&hoursBack=1")]
    [InlineData("?carParkCode=this-code-is-longer-than-fifty-characters-so-it-fails&hoursBack=1")]
    [InlineData("?carParkCode=CP1")]
    [InlineData("?carParkCode=CP1&from=2026-01-01T00:00:00Z")]
    [InlineData("?carParkCode=CP1&to=2026-01-01T00:00:00Z")]
    [InlineData("?carParkCode=CP1&hoursBack=0")]
    [InlineData("?carParkCode=CP1&hoursBack=-1")]
    [InlineData("?carParkCode=CP1&hoursBack=1&from=2026-01-01T00:00:00Z&to=2026-01-01T01:00:00Z")]
    [InlineData("?carParkCode=CP1&from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    public async Task History_BadInput_BadRequest(string query)
    {
        var (c, _, _) = Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/occupancy/history" + query)).StatusCode);
    }

    [Fact]
    public void SampleWindow_ExtendsPriorUntil_ThroughSameWeekdayEmptying()
    {
        var hk = TimeSpan.FromHours(8);
        var noon = new DateTimeOffset(2026, 11, 15, 12, 0, 0, hk);
        var noonWindow = OccupancyForecast.SampleWindow(noon);
        Assert.Equal(noon.AddHours(-OccupancyForecast.RecentHours), noonWindow.RecentSince);
        Assert.Equal(noon.AddHours(-(OccupancyForecast.WeekHours + OccupancyForecast.RecentHours)), noonWindow.PriorSince);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 8, 0, 0, hk), noonWindow.PriorUntil);
        Assert.True(noonWindow.PriorUntil > noon.AddHours(-OccupancyForecast.WeekHours).AddHours(OccupancyForecast.HorizonHours));
        Assert.True(new DateTimeOffset(2026, 11, 9, 5, 0, 0, hk) <= noonWindow.PriorUntil);
        Assert.True(new DateTimeOffset(2026, 11, 9, 9, 0, 0, hk) > noonWindow.PriorUntil);
        Assert.True((noonWindow.PriorUntil - new DateTimeOffset(2026, 11, 8, 17, 0, 0, hk)).TotalHours * 4 <= OccupancyForecast.WindowPoints);

        var late = new DateTimeOffset(2026, 11, 15, 22, 0, 0, hk);
        var lateWindow = OccupancyForecast.SampleWindow(late);
        var lateWeekAgo = late.AddHours(-OccupancyForecast.WeekHours);
        Assert.Equal(late.AddHours(-OccupancyForecast.RecentHours), lateWindow.RecentSince);
        Assert.Equal(lateWeekAgo.AddHours(-OccupancyForecast.RecentHours), lateWindow.PriorSince);
        Assert.True(lateWindow.PriorUntil > lateWeekAgo.AddHours(OccupancyForecast.HorizonHours));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 8, 0, 0, hk), lateWindow.PriorUntil);

        var early = new DateTimeOffset(2026, 11, 16, 1, 0, 0, hk);
        var earlyWindow = OccupancyForecast.SampleWindow(early);
        Assert.Equal(early.AddHours(-OccupancyForecast.RecentHours), earlyWindow.RecentSince);
        Assert.Equal(early.AddHours(-(OccupancyForecast.WeekHours + OccupancyForecast.RecentHours)), earlyWindow.PriorSince);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, hk), earlyWindow.PriorUntil);

        var noonUtc = new DateTimeOffset(2026, 11, 15, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(noonWindow.PriorUntil, OccupancyForecast.SampleWindow(noonUtc).PriorUntil);
    }

    [Fact]
    public void BuildPrompt_LabelsLastWeekCurve_AndOmitsWhenThosePointsAreMissing()
    {
        var hk = TimeSpan.FromHours(8);
        var latest = new DateTimeOffset(2026, 11, 15, 12, 7, 0, hk);
        var weekAgo = latest.AddHours(-OccupancyForecast.WeekHours);
        OccupancyPoint At(DateTimeOffset t, int level) => new()
        {
            SnapshotTime = t,
            CountingCategory = "Total",
            CurrentLevel = level,
            Capacity = 400
        };

        var points = new List<OccupancyPoint>
        {
            At(weekAgo, 200),
            At(weekAgo.AddHours(4), 250),
            At(new DateTimeOffset(2026, 11, 8, 17, 0, 0, hk), 380),
            At(new DateTimeOffset(2026, 11, 8, 20, 0, 0, hk), 140),
            At(new DateTimeOffset(2026, 11, 9, 2, 0, 0, hk), 45),
            At(latest.AddMinutes(-15), 205),
            At(latest, 210)
        };

        var prompt = OccupancyForecast.BuildPrompt("CP1", "Total", points);
        Assert.Contains("through the later of 6 hours after it and 02:00 the next morning", prompt);
        Assert.Contains("last 48 hours at about 15-minute cadence", prompt);
        Assert.Contains("Do not predict a currentLevel above 400.", prompt);
        Assert.Contains("\"t\":\"Sun 2026-11-15 11:52:00 +08:00\",\"level\":205,\"cap\":400", prompt);
        Assert.Contains("\"t\":\"Sun 2026-11-15 12:07:00 +08:00\",\"level\":210,\"cap\":400", prompt);
        var older = prompt.IndexOf("\"level\":205", StringComparison.Ordinal);
        var newer = prompt.IndexOf("\"level\":210", StringComparison.Ordinal);
        Assert.True(older >= 0 && older < newer);

        Assert.Contains("Last week's same-weekday emptying curve (hourly actuals, Asia/Hong_Kong, 17:00 through 02:00 the next morning):", prompt);
        Assert.Contains("Sunday 2026-11-08 17:00 level 380 cap 400", prompt);
        Assert.Contains("Sunday 2026-11-08 20:00 level 140 cap 400", prompt);
        Assert.Contains("Monday 2026-11-09 02:00 level 45 cap 400", prompt);
        Assert.Contains("Use this curve for emptying: when the drop starts, how fast it falls, and how much occupancy remains after it bottoms out. Do not predict 0 at an hour offset where last week still had residual cars. Do not smooth away the evening drop.", prompt);
        Assert.DoesNotContain("Hard rule:", prompt);

        Assert.Contains("Last week at this clock time and the next 6 hours (Asia/Hong_Kong):", prompt);
        Assert.Contains("Sunday 2026-11-08 12:07 level 200 cap 400", prompt);
        Assert.Contains("Sunday 2026-11-08 16:07 level 250 cap 400", prompt);

        var recentOnly = OccupancyForecast.BuildPrompt("CP1", "Total", [At(latest, 210)]);
        Assert.Contains("\"t\":\"Sun 2026-11-15 12:07:00 +08:00\",\"level\":210,\"cap\":400", recentOnly);
        Assert.Contains("15-minute cadence", recentOnly);
        Assert.DoesNotContain("emptying curve", recentOnly);
        Assert.DoesNotContain("this clock time and the next 6 hours", recentOnly);
        Assert.DoesNotContain("Do not predict 0", recentOnly);
    }

    [Fact]
    public async Task Predict_WithHistory_ReturnsSixHours()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 10, hoursAgo: 3));
        store.Rows.Add(Point("CP1", "Total", 40, capacity: 82, hoursAgo: 1));
        store.Rows.Add(Point("CP1", "Total", 77, hoursAgo: 0.1, carparkNo: 1));

        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal("CP1", json.GetProperty("carParkCode").GetString());
        Assert.Equal("Total", json.GetProperty("countingCategory").GetString());
        Assert.Equal("fake-model", json.GetProperty("model").GetString());

        var forecasts = json.GetProperty("forecasts");
        Assert.Equal(6, forecasts.GetArrayLength());
        Assert.Equal(1, forecasts[0].GetProperty("hourOffset").GetInt32());
        Assert.Equal(41, forecasts[0].GetProperty("currentLevel").GetInt32());
        Assert.Equal(50, forecasts[0].GetProperty("occupancyPercent").GetDouble(), 1);
        Assert.Equal(6, forecasts[5].GetProperty("hourOffset").GetInt32());
        Assert.Equal(46, forecasts[5].GetProperty("currentLevel").GetInt32());

        var older = gemini.LastPrompt!.IndexOf("\"level\":10", StringComparison.Ordinal);
        var newer = gemini.LastPrompt.IndexOf("\"level\":40", StringComparison.Ordinal);
        Assert.True(older >= 0 && older < newer);
        Assert.DoesNotContain("\"level\":77", gemini.LastPrompt);
    }

    [Fact]
    public async Task Predict_SteepRecentSlope_IsEnforced_StableIsNotRamped()
    {
        var (c, store, gemini) = Create();
        var now = DateTimeOffset.UtcNow;

        void Quarters(string code, params int[] levelsOldestFirst)
        {
            for (var i = 0; i < levelsOldestFirst.Length; i++)
            {
                store.Rows.Add(Point(code, "Total", levelsOldestFirst[i], capacity: 500) with
                {
                    SnapshotTime = now.AddMinutes(-15 * (levelsOldestFirst.Length - 1 - i))
                });
            }
        }

        Quarters("CLIMB", 100, 130, 160, 190);
        Quarters("DROP", 300, 275, 250, 225);
        Quarters("STABLE", 40, 41, 41, 42);

        gemini.Text = """
            {"forecasts":[
              {"hourOffset":1,"currentLevel":200},
              {"hourOffset":2,"currentLevel":210},
              {"hourOffset":3,"currentLevel":220},
              {"hourOffset":4,"currentLevel":230},
              {"hourOffset":5,"currentLevel":240},
              {"hourOffset":6,"currentLevel":250}
            ]}
            """;
        var climbRes = await c.GetAsync("/api/occupancy/predict?carParkCode=CLIMB");
        Assert.Equal(HttpStatusCode.OK, climbRes.StatusCode);
        var climb = ForecastLevels(await Body(climbRes));
        Assert.Equal(new[] { 310, 430, 500, 500, 500, 500 }, climb);
        Assert.Contains("must be 310,430,500,500,500,500", gemini.LastPrompt);

        gemini.Text = """
            {"forecasts":[
              {"hourOffset":1,"currentLevel":210},
              {"hourOffset":2,"currentLevel":200},
              {"hourOffset":3,"currentLevel":190},
              {"hourOffset":4,"currentLevel":180},
              {"hourOffset":5,"currentLevel":170},
              {"hourOffset":6,"currentLevel":160}
            ]}
            """;
        var dropRes = await c.GetAsync("/api/occupancy/predict?carParkCode=DROP");
        Assert.Equal(HttpStatusCode.OK, dropRes.StatusCode);
        var drop = ForecastLevels(await Body(dropRes));
        Assert.Equal(new[] { 125, 25, 0, 0, 0, 0 }, drop);
        Assert.Contains("must be 125,25,0,0,0,0", gemini.LastPrompt);

        gemini.Text = SixHours;
        var stableRes = await c.GetAsync("/api/occupancy/predict?carParkCode=STABLE");
        Assert.Equal(HttpStatusCode.OK, stableRes.StatusCode);
        var stable = ForecastLevels(await Body(stableRes));
        Assert.Equal(new[] { 41, 42, 43, 44, 45, 46 }, stable);
        Assert.DoesNotContain("must be", gemini.LastPrompt);
    }

    [Fact]
    public async Task Predict_ResidualFloor_LiftsSlopeZeros_AndDoesNotRebound()
    {
        var (c, store, gemini) = Create();
        gemini.Text = SixHours;
        var now = DateTimeOffset.UtcNow;
        var hk = now.ToOffset(TimeSpan.FromHours(8));
        var weekAgo = hk.AddHours(-OccupancyForecast.WeekHours);
        var start = new DateTimeOffset(weekAgo.Year, weekAgo.Month, weekAgo.Day, 17, 0, 0, hk.Offset);
        var low = start.AddHours(12);

        void Row(string code, DateTimeOffset t, int level, int capacity) =>
            store.Rows.Add(Point(code, "Total", level, capacity) with { SnapshotTime = t });

        void Quarters(string code, int capacity, params int[] levelsOldestFirst)
        {
            for (var i = 0; i < levelsOldestFirst.Length; i++)
                Row(code, now.AddMinutes(-15 * (levelsOldestFirst.Length - 1 - i)), levelsOldestFirst[i], capacity);
        }

        void Overnight(string code, int capacity, int evening, int bottom)
        {
            Row(code, start, evening, capacity);
            Row(code, start.AddHours(2), evening - 10, capacity);
            Row(code, low, bottom, capacity);
            Row(code, start.AddHours(15), bottom + 1, capacity);
        }

        Quarters("LIFT", 85, 79, 70, 60, 50);
        Overnight("LIFT", 85, 70, 8);
        Quarters("ABOVE", 100, 40, 40, 40, 40);
        Overnight("ABOVE", 100, 90, 8);
        Quarters("BELOW", 100, 34, 24, 15, 5);
        Overnight("BELOW", 100, 80, 14);
        Quarters("MISS", 85, 79, 70, 60, 50);
        Row("MISS", start.AddHours(-2), 1, 85);
        Row("MISS", start.AddHours(16), 1, 85);

        var liftRes = await c.GetAsync("/api/occupancy/predict?carParkCode=LIFT");
        Assert.Equal(HttpStatusCode.OK, liftRes.StatusCode);
        var liftJson = await Body(liftRes);
        Assert.Equal(new[] { 11, 8, 8, 8, 8, 8 }, ForecastLevels(liftJson));
        var liftHours = liftJson.GetProperty("forecasts");
        Assert.Equal(Math.Round(100.0 * 11 / 85, 1), liftHours[0].GetProperty("occupancyPercent").GetDouble(), 3);
        Assert.Equal(Math.Round(100.0 * 8 / 85, 1), liftHours[1].GetProperty("occupancyPercent").GetDouble(), 3);

        var aboveRes = await c.GetAsync("/api/occupancy/predict?carParkCode=ABOVE");
        Assert.Equal(HttpStatusCode.OK, aboveRes.StatusCode);
        var aboveJson = await Body(aboveRes);
        Assert.Equal(new[] { 41, 42, 43, 44, 45, 46 }, ForecastLevels(aboveJson));
        Assert.Equal(41.0, aboveJson.GetProperty("forecasts")[0].GetProperty("occupancyPercent").GetDouble(), 3);

        var belowRes = await c.GetAsync("/api/occupancy/predict?carParkCode=BELOW");
        Assert.Equal(HttpStatusCode.OK, belowRes.StatusCode);
        Assert.Equal(new[] { 5, 5, 5, 5, 5, 5 }, ForecastLevels(await Body(belowRes)));

        var missRes = await c.GetAsync("/api/occupancy/predict?carParkCode=MISS");
        Assert.Equal(HttpStatusCode.OK, missRes.StatusCode);
        Assert.Equal(new[] { 11, 0, 0, 0, 0, 0 }, ForecastLevels(await Body(missRes)));
    }

    [Fact]
    public async Task Predict_FencedJson_AndZeroCapacity()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 5, capacity: 0));
        gemini.Text = "```json\n" + SixHours + "\n```";

        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        var json = await Body(res);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("forecasts")[0].GetProperty("occupancyPercent").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?carParkCode=%20")]
    [InlineData("?carParkCode=this-code-is-longer-than-fifty-characters-so-it-fails")]
    public async Task Predict_BadCarParkCode_BadRequest(string query)
    {
        var (c, _, gemini) = Create();
        var res = await c.GetAsync("/api/occupancy/predict" + query);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null(gemini.LastPrompt);
    }

    [Fact]
    public async Task Predict_NoHistory_NotFound()
    {
        var (c, store, _) = Create();
        store.Rows.Add(Point("CP1", "Total", 10, hoursAgo: 100));
        store.Rows.Add(Point("OTHER", "Total", 10));
        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Predict_DefaultsToTotal_AndRejectsOtherCategories()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 10));
        store.Rows.Add(Point("CP1", "EV", 90));

        var ev = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1&countingCategory=EV");
        Assert.Equal(HttpStatusCode.BadRequest, ev.StatusCode);
        Assert.Equal("countingCategory must be Total.", (await Body(ev)).GetProperty("error").GetString());
        Assert.Null(gemini.LastPrompt);

        var otherCase = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1&countingCategory=total");
        Assert.Equal(HttpStatusCode.BadRequest, otherCase.StatusCode);
        Assert.Null(gemini.LastPrompt);

        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal("Total", json.GetProperty("countingCategory").GetString());
        Assert.Contains("\"level\":10", gemini.LastPrompt);
        Assert.DoesNotContain("\"level\":90", gemini.LastPrompt);

        var explicitTotal = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1&countingCategory=Total");
        Assert.Equal(HttpStatusCode.OK, explicitTotal.StatusCode);
        Assert.Equal("Total", (await Body(explicitTotal)).GetProperty("countingCategory").GetString());
    }

    [Fact]
    public async Task Predict_NoTotalHistory_NotFound()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "EV", 10));
        store.Rows.Add(Point("CP1", "Total", 50, carparkNo: 1));

        var omitted = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.NotFound, omitted.StatusCode);
        Assert.Null(gemini.LastPrompt);

        var explicitTotal = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1&countingCategory=Total");
        Assert.Equal(HttpStatusCode.NotFound, explicitTotal.StatusCode);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("""{"forecasts":[{"hourOffset":1,"currentLevel":1}]}""")]
    public async Task Predict_UnreadableForecast_BadGateway(string text)
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 10));
        gemini.Text = text;
        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    [Fact]
    public async Task Predict_GeminiFailure_BadGateway()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 10));
        gemini.Error = new GeminiException((int)HttpStatusCode.BadGateway, "Gemini request failed.");
        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("Gemini request failed.", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Predict_MissingApiKey_ServiceUnavailable()
    {
        var previousPrimary = Environment.GetEnvironmentVariable("Gemini__ApiKey");
        var previousGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var previousAlias = Environment.GetEnvironmentVariable("Vertex__ApiKey");
        Environment.SetEnvironmentVariable("Gemini__ApiKey", "present-but-cleared-by-config");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "present-but-cleared-by-config");
        Environment.SetEnvironmentVariable("Vertex__ApiKey", "present-but-cleared-by-config");
        try
        {
            var store = new FakeStore();
            store.Rows.Add(Point("CP1", "Total", 40));
            var client = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Gemini:ApiKey"] = "",
                            ["GEMINI_API_KEY"] = "",
                            ["Vertex:ApiKey"] = "",
                            ["ApiKey"] = TestApiKey
                        });
                    });
                    b.ConfigureServices(s => s.AddSingleton<IOccupancyStore>(store));
                })
                .CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", TestApiKey);

            var res = await client.GetAsync("/api/occupancy/predict?carParkCode=CP1");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Contains("Gemini__ApiKey", await res.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Gemini__ApiKey", previousPrimary);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousGemini);
            Environment.SetEnvironmentVariable("Vertex__ApiKey", previousAlias);
        }
    }

    [Fact]
    public async Task GeminiClient_UsesPublisherApiKeyQuery()
    {
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        var handler = new StubHandler(async (req, ct) =>
        {
            captured = req;
            requestBody = await req.Content!.ReadAsStringAsync(ct);
            var json = """{"candidates":[{"content":{"parts":[{"text":"thinking"},{"text":"{\"ok\":true}"}]}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gemini:ApiKey"] = "publisher-key",
            ["GEMINI_API_KEY"] = "studio-key",
            ["Vertex:ApiKey"] = "alias-key"
        }).Build();

        using var http = new HttpClient(handler);
        var reply = await new GeminiClient(http, config).GenerateAsync("history", CancellationToken.None);

        Assert.Equal("gemini-3.5-flash-lite", reply.Model);
        Assert.Equal("{\"ok\":true}", reply.Text);
        Assert.False(captured!.Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("studio-key", captured.RequestUri!.ToString());
        Assert.DoesNotContain("alias-key", captured.RequestUri.ToString());
        Assert.Equal("https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-3.5-flash-lite:generateContent?key=publisher-key", captured.RequestUri.AbsoluteUri);
        Assert.DoesNotContain("publisher-key", requestBody);
        Assert.Contains("\"text\":\"history\"", requestBody);
        Assert.Contains("application/json", requestBody);
    }

    [Fact]
    public async Task GeminiClient_AliasKey_AndConfiguredModel()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            var json = """{"candidates":[{"content":{"parts":[{"text":"[]"}]}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vertex:ApiKey"] = "alias-key",
            ["Vertex:Model"] = "gemini-3.8-flash-lite"
        }).Build();

        using var http = new HttpClient(handler);
        var reply = await new GeminiClient(http, config).GenerateAsync("history", CancellationToken.None);
        Assert.Equal("gemini-3.8-flash-lite", reply.Model);
        Assert.False(captured!.Headers.Contains("x-goog-api-key"));
        Assert.Equal("https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-3.8-flash-lite:generateContent?key=alias-key", captured.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GeminiClient_HttpError_AndMissingKey()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("nope") });
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GEMINI_API_KEY"] = "k"
        }).Build();

        using var http = new HttpClient(handler);
        var failed = await Assert.ThrowsAsync<GeminiException>(() => new GeminiClient(http, config).GenerateAsync("p", CancellationToken.None));
        Assert.Equal((int)HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("429", failed.Message);
        Assert.DoesNotContain("nope", failed.Message);
        Assert.Equal("https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-3.5-flash-lite:generateContent?key=k", captured!.RequestUri!.AbsoluteUri);

        var missing = await Assert.ThrowsAsync<GeminiException>(() =>
            new GeminiClient(http, new ConfigurationBuilder().Build()).GenerateAsync("p", CancellationToken.None));
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Contains("Gemini__ApiKey", missing.Message);
    }
}
