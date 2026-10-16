using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

public class ApiTests
{
    class FakeStore : IOccupancyStore
    {
        public List<OccupancyRow> Rows = [];
        public Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows) { Rows.AddRange(rows); return Task.CompletedTask; }
    }

    static (HttpClient, FakeStore) Create()
    {
        var store = new FakeStore();
        var client = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IOccupancyStore>(store)))
            .CreateClient();
        return (client, store);
    }

    const string Row = """{"CarParkCode":"{0}","SnapshotTime":"2026-01-01T10:00:00+08:00","CarparkNo":1,"CarparkDesig":"A","CountingCategoryNo":1,"CountingCategory":"Total","FreeLimit":5,"OccupancyLimit":95,"CurrentLevel":40,"Capacity":100}""";

    static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var (c, _) = Create();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Ingest_Valid_Persists()
    {
        var (c, store) = Create();
        var res = await c.PostAsync("/api/occupancy", Json($"[{Row.Replace("{0}", "CP1")}]"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(store.Rows);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"SnapshotTime":"2026-01-01T10:00:00+08:00"}]""")]
    public async Task Ingest_Invalid_BadRequest(string body)
    {
        var (c, store) = Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/occupancy", Json(body))).StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Ingest_BlankCarParkCode_BadRequest()
    {
        var (c, _) = Create();
        var res = await c.PostAsync("/api/occupancy", Json($"[{Row.Replace("{0}", " ")}]"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
