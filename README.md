# Car Park Occupancy Data API

Minimal ASP.NET Core (.NET 10) API that ingests car park occupancy snapshots into a central SQL Server DB.

## Run
1. Create the table: run `db/001_OccupancySnapshot.sql` against your DB (creates `dbo.OccupancySnapshot`).
2. Set the connection string (never commit it):
   `export ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"`
   (or `ConnectionStrings:CentralDb` in configuration/user-secrets)
3. `dotnet run --project src/CarParkOccupancy.DataApi`

## Endpoints
- `GET /health` → `200 {"status":"ok"}`
- `POST /api/occupancy` — JSON array of rows, all fields required:
  ```json
  [{"CarParkCode":"CP1","SnapshotTime":"2026-01-01T10:00:00+08:00","CarparkNo":1,"CarparkDesig":"A",
    "CountingCategoryNo":1,"CountingCategory":"Total","FreeLimit":5,"OccupancyLimit":95,"CurrentLevel":40,"Capacity":100}]
  ```
  Returns `200 {"inserted":N}` (batch inserted in one transaction) or `400 {"error":...}` on validation failure
  (empty array, missing field, blank/over-long strings).

## Test
`dotnet test` — uses an in-memory fake `IOccupancyStore`; no SQL Server needed. The real SQL insert is not covered by tests.
