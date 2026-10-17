# Car Park Occupancy Data API

Minimal ASP.NET Core (.NET 10) API that ingests car park occupancy snapshots into a central SQL Server DB and forecasts the next 6 hours of `CurrentLevel` with the Gemini API.

## Run
1. Create the table: run `db/001_OccupancySnapshot.sql` against your DB (creates `dbo.OccupancySnapshot`).
2. Set the connection string (never commit it):
   `export ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"`
   (or `ConnectionStrings:CentralDb` in configuration/user-secrets)
3. Set the occupancy API key (never commit it). `POST /api/occupancy`, `GET /api/occupancy/latest`, and `GET /api/occupancy/predict` require header `X-Api-Key`. `GET /health` does not.
   `export ApiKey="..."`
   Configuration key `ApiKey`. The environment variable `ApiKey` overrides it. If the value is missing or blank, those routes return `401 {"error":"Unauthorized."}`.
4. For forecasts, set a Gemini API key (never commit it, never bake it into an image):
   `export GEMINI_API_KEY="..."`
   Alias, used only when `GEMINI_API_KEY` is unset: `Vertex__ApiKey`.
   Optional model override: `Vertex__Model` (default `gemini-3.8-flash`).
5. `dotnet run --project src/CarParkOccupancy.DataApi`

Forecast auth is a plain API key on the Gemini API (Google AI Studio), not a service-account file and not Application Default Credentials. Project id and location are not used.

The call is:

`POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`

with header `x-goog-api-key`. The key is not put in the query string.

## Docker

The image listens on port **8080** (ASP.NET Core's default in containers). `ConnectionStrings__CentralDb`, `ApiKey`, and `GEMINI_API_KEY` are not baked in; pass them at run time. Compose also forwards `Vertex__ApiKey` and `Vertex__Model` when set. Compose runs the API only (no SQL Server).

```bash
docker build -t carpark-occupancy-api .
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" \
  -e ApiKey \
  -e GEMINI_API_KEY \
  carpark-occupancy-api
```

```bash
export ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"
export ApiKey="..."
export GEMINI_API_KEY="..."
docker compose up --build
```

Health: `GET http://localhost:8080/health` → `200 {"status":"ok"}`. (`dotnet run` still uses the ports in `launchSettings.json`.)

## Endpoints
Occupancy routes require header `X-Api-Key` with the same value as `ApiKey`. A missing header, a wrong key, or an unset `ApiKey` returns `401 {"error":"Unauthorized."}`.

- `GET /health` → `200 {"status":"ok"}` (no API key)
- `POST /api/occupancy` — JSON array of rows, all fields required:
  ```json
  [{"CarParkCode":"CP1","SnapshotTime":"2026-01-01T10:00:00+08:00","CarparkNo":1,"CarparkDesig":"A",
    "CountingCategoryNo":1,"CountingCategory":"Total","FreeLimit":5,"OccupancyLimit":95,"CurrentLevel":40,"Capacity":100}]
  ```
  Returns `200 {"inserted":N}` (batch inserted in one transaction) or `400 {"error":...}` on validation failure
  (empty array, missing field, blank/over-long strings).
- `GET /api/occupancy/latest` — one latest row per `CarParkCode` + `CountingCategory` (newest `SnapshotTime`, then highest `Id`). `200` JSON array, camelCase, same fields as ingest; `[]` when the table is empty.
  ```json
  [{"carParkCode":"CP1","snapshotTime":"2026-01-01T10:00:00+08:00","carparkNo":1,"carparkDesig":"A",
    "countingCategoryNo":1,"countingCategory":"Total","freeLimit":5,"occupancyLimit":95,"currentLevel":40,"capacity":100}]
  ```
- `GET /api/occupancy/predict?carParkCode=CP1&countingCategory=Total`
  - `countingCategory` is optional when the car park has only one category in the history window.
  - History window: snapshots with `SnapshotTime` in the **last 48 hours**, at most **200** most recent rows for that series (sent to the model oldest-first). No new tables.
  - `200` body:
    ```json
    {"carParkCode":"CP1","countingCategory":"Total","model":"gemini-3.8-flash",
     "forecasts":[{"hourOffset":1,"currentLevel":41,"occupancyPercent":41.0}]}
    ```
    Six items, `hourOffset` 1–6. `occupancyPercent` is `currentLevel / latest Capacity * 100` (1 decimal), or `null` when that capacity is 0.
  - `400` bad input, or more than one category and `countingCategory` was omitted.
  - `404` no rows in the window.
  - `502` Gemini HTTP/transport failure or an unreadable forecast. `503` when the Gemini API key is missing or `Vertex__Model` is not a plain model id.

`dotnet test` stubs history and Gemini and sends a test `X-Api-Key`. It does not call the live API and does not need `ApiKey` or `GEMINI_API_KEY` in the environment. A live check is a request with `X-Api-Key` against a running API that has `ApiKey` and `GEMINI_API_KEY` set and rows already in `dbo.OccupancySnapshot`.
