using Dapper;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IOccupancyStore, SqlOccupancyStore>();
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

public interface IOccupancyStore
{
    Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows);
}

public class SqlOccupancyStore(IConfiguration config) : IOccupancyStore
{
    private const string Sql = """
        INSERT INTO dbo.OccupancySnapshot
            (CarParkCode, SnapshotTime, CarparkNo, CarparkDesig, CountingCategoryNo, CountingCategory, FreeLimit, OccupancyLimit, CurrentLevel, Capacity)
        VALUES
            (@CarParkCode, @SnapshotTime, @CarparkNo, @CarparkDesig, @CountingCategoryNo, @CountingCategory, @FreeLimit, @OccupancyLimit, @CurrentLevel, @Capacity)
        """;

    public async Task InsertAsync(IReadOnlyCollection<OccupancyRow> rows)
    {
        await using var conn = new SqlConnection(config.GetConnectionString("CentralDb"));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(Sql, rows, tx);
        await tx.CommitAsync();
    }
}

public partial class Program;
