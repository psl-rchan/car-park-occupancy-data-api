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
        public Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows) { Rows.AddRange(rows); return Task.CompletedTask; }

        public Task<IReadOnlyList<OccupancyRow>> GetLatestAsync()
        {
            IReadOnlyList<OccupancyRow> latest = Rows
                .Select((row, index) => (row, index))
                .GroupBy(x => (x.row.CarParkCode, x.row.CountingCategory))
                .Select(g => g.OrderByDescending(x => x.row.SnapshotTime).ThenByDescending(x => x.index).First().row)
                .OrderBy(r => r.CarParkCode, StringComparer.Ordinal)
                .ThenBy(r => r.CountingCategory, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(latest);
        }

        public Task<RecentOccupancy> GetRecentAsync(string carParkCode, string? countingCategory)
        {
            var since = DateTimeOffset.UtcNow.AddHours(-OccupancyForecast.HistoryHours);
            var window = Rows
                .Where(r => r.CarParkCode == carParkCode && r.SnapshotTime >= since)
                .Select(r => new OccupancyPoint
                {
                    SnapshotTime = r.SnapshotTime,
                    CountingCategory = r.CountingCategory,
                    CurrentLevel = r.CurrentLevel,
                    Capacity = r.Capacity
                })
                .ToList();

            var categories = window.Select(p => p.CountingCategory).Distinct().ToList();
            if (categories.Count == 0)
                return Task.FromResult(new RecentOccupancy([], []));
            if (countingCategory is null && categories.Count > 1)
                return Task.FromResult(new RecentOccupancy(categories, []));

            var category = countingCategory ?? categories[0];
            var points = window
                .Where(p => p.CountingCategory == category)
                .OrderByDescending(p => p.SnapshotTime)
                .Take(OccupancyForecast.MaxPoints)
                .OrderBy(p => p.SnapshotTime)
                .ToList();
            return Task.FromResult(new RecentOccupancy(points.Count == 0 ? [] : [category], points));
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

    static OccupancyRow Point(string code, string category, int level, int capacity = 100, double hoursAgo = 1) => new()
    {
        CarParkCode = code,
        SnapshotTime = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
        CarparkNo = 1,
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync("/api/occupancy/predict?carParkCode=CP1")).StatusCode);
        Assert.Null(gemini.LastPrompt);

        var (wrong, _, _) = Create(sendKey: false);
        wrong.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/occupancy/latest")).StatusCode);
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
    public async Task Latest_ReturnsNewestRowPerCarParkAndCategory()
    {
        var (c, store, _) = Create();
        store.Rows.Add(Point("CP1", "Total", 10, hoursAgo: 3));
        store.Rows.Add(Point("CP1", "Total", 40, capacity: 82, hoursAgo: 1));
        store.Rows.Add(Point("CP2", "Total", 3, hoursAgo: 2));
        store.Rows.Add(Point("CP1", "EV", 7, hoursAgo: 2));

        var res = await c.GetAsync("/api/occupancy/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal(3, json.GetArrayLength());

        Assert.Equal("CP1", json[0].GetProperty("carParkCode").GetString());
        Assert.Equal("EV", json[0].GetProperty("countingCategory").GetString());
        Assert.Equal(7, json[0].GetProperty("currentLevel").GetInt32());

        Assert.Equal("CP1", json[1].GetProperty("carParkCode").GetString());
        Assert.Equal("Total", json[1].GetProperty("countingCategory").GetString());
        Assert.Equal(40, json[1].GetProperty("currentLevel").GetInt32());
        Assert.Equal(82, json[1].GetProperty("capacity").GetInt32());

        Assert.Equal("CP2", json[2].GetProperty("carParkCode").GetString());
        Assert.Equal("Total", json[2].GetProperty("countingCategory").GetString());
        Assert.Equal(3, json[2].GetProperty("currentLevel").GetInt32());
    }

    [Fact]
    public async Task Predict_WithHistory_ReturnsSixHours()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 10, hoursAgo: 3));
        store.Rows.Add(Point("CP1", "Total", 40, capacity: 82, hoursAgo: 1));

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
    public async Task Predict_TwoCategories_RequiresCategory()
    {
        var (c, store, gemini) = Create();
        store.Rows.Add(Point("CP1", "Total", 10));
        store.Rows.Add(Point("CP1", "EV", 90));

        var missing = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Null(gemini.LastPrompt);

        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1&countingCategory=EV");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Body(res);
        Assert.Equal("EV", json.GetProperty("countingCategory").GetString());
        Assert.Contains("\"level\":90", gemini.LastPrompt);
        Assert.DoesNotContain("\"level\":10", gemini.LastPrompt);
    }

    [Fact]
    public async Task Predict_UnknownCategory_NotFound()
    {
        var (c, store, _) = Create();
        store.Rows.Add(Point("CP1", "Total", 10));
        var res = await c.GetAsync("/api/occupancy/predict?carParkCode=CP1&countingCategory=EV");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
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

        Assert.Equal("gemini-2.5-flash-lite", reply.Model);
        Assert.Equal("{\"ok\":true}", reply.Text);
        Assert.False(captured!.Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("studio-key", captured.RequestUri!.ToString());
        Assert.DoesNotContain("alias-key", captured.RequestUri.ToString());
        Assert.Equal("https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-2.5-flash-lite:generateContent?key=publisher-key", captured.RequestUri.AbsoluteUri);
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
        Assert.Equal("https://aiplatform.googleapis.com/v1/publishers/google/models/gemini-2.5-flash-lite:generateContent?key=k", captured!.RequestUri!.AbsoluteUri);

        var missing = await Assert.ThrowsAsync<GeminiException>(() =>
            new GeminiClient(http, new ConfigurationBuilder().Build()).GenerateAsync("p", CancellationToken.None));
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Contains("Gemini__ApiKey", missing.Message);
    }
}
