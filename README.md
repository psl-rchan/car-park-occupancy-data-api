# Car Park Occupancy Data API

Minimal ASP.NET Core (.NET 10) API that ingests car park occupancy snapshots into a central SQL Server DB and forecasts the next 6 hours of `CurrentLevel` with the Gemini API.

## Run
1. Create the table: run `db/001_OccupancySnapshot.sql` against your DB (creates `dbo.OccupancySnapshot`), then `db/002_OccupancySnapshot_Indexes.sql` and `db/003_OccupancySnapshot_History_Index.sql`.
2. Set the connection string (never commit it):
   `export ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"`
   (or `ConnectionStrings:CentralDb` in configuration/user-secrets)
3. Set the occupancy API key (never commit it). `POST /api/occupancy`, `GET /api/occupancy/latest`, `GET /api/occupancy/predict`, and `POST /api/occupancy/official-hourly` require header `X-Api-Key`. `GET /health` does not.
   `export ApiKey="..."`
   Configuration key `ApiKey`. The environment variable `ApiKey` overrides it. If the value is missing or blank, those routes return `401 {"error":"Unauthorized."}`.
4. For forecasts, set a Gemini API key (never commit it, never bake it into an image):
   `export Gemini__ApiKey="..."`
   Configuration key `Gemini:ApiKey`. This is the primary name.
   When `Gemini__ApiKey` is unset, these existing names still work: `GEMINI_API_KEY`, then `Vertex__ApiKey`.
   Optional model override: `Vertex__Model` (default `gemini-3.5-flash-lite`).
5. `dotnet run --project src/CarParkOccupancy.DataApi`

Forecast auth is an API key on the Vertex AI publisher `generateContent` endpoint, not a service-account file and not Application Default Credentials. Project id and location are not used.

The call is:

`POST https://aiplatform.googleapis.com/v1/publishers/google/models/{model}:generateContent?key={api_key}`

The key is the `key` query parameter. It is not sent in an `x-goog-api-key` header.

## Docker

The image listens on port **8080** (ASP.NET Core's default in containers). `ConnectionStrings__CentralDb`, `ApiKey`, and `Gemini__ApiKey` are not baked in; pass them at run time. Compose also forwards `GEMINI_API_KEY`, `Vertex__ApiKey`, and `Vertex__Model` when set, so a host that still exports those older names keeps working. Compose runs the API only (no SQL Server).

```bash
docker build -t carpark-occupancy-api .
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" \
  -e ApiKey \
  -e Gemini__ApiKey \
  carpark-occupancy-api
```

```bash
export ConnectionStrings__CentralDb="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"
export ApiKey="..."
export Gemini__ApiKey="..."
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
  (empty array, missing field, blank/over-long strings). Any `CountingCategory` is accepted.
- `GET /api/occupancy/latest` — one latest row per `CarParkCode` where `CountingCategory` is exactly `Total` and `CarparkNo` is `0` (newest `SnapshotTime`, then highest `Id`). Other categories and car park numbers are omitted. Only parks with a matching open (`IsClosed = 0`) `dbo.CarPark` row are returned (`INNER JOIN` on `CarParkCode`). `200` JSON array, camelCase, same occupancy fields as ingest, plus nullable catalog fields (`area`, `district`, `subDistrict`, `areaEn`, `districtEn`, `subDistrictEn`, `name`, `nameEn`; null when the column is null); `[]` when no matching row exists.
  ```json
  [{"carParkCode":"CP1","snapshotTime":"2026-01-01T10:00:00+08:00","carparkNo":0,"carparkDesig":"A",
    "countingCategoryNo":1,"countingCategory":"Total","freeLimit":5,"occupancyLimit":95,"currentLevel":40,"capacity":100,
    "area":"香港島","district":"中西區","subDistrict":null,"areaEn":"Hong Kong Island","districtEn":"Central and Western","subDistrictEn":null,"name":null,"nameEn":null}]
  ```
- `GET /api/occupancy/predict?carParkCode=CP1&countingCategory=Total`
  - Forecasts the Total series for `CarparkNo` `0` only. `countingCategory` is optional and defaults to `Total`. Any other value returns `400 {"error":"countingCategory must be Total."}`.
  - History sent oldest-first for `CarparkNo` `0`: the **last 48 hours** at about 15-minute cadence, and the **same clock hours one week earlier** (from 48 hours before that instant through the later of 6 hours after it, 02:00 the next morning, and 08:00 the next morning, Asia/Hong_Kong). The labeled emptying curve stays hourly from 17:00 through 02:00; the prior block is extended through 08:00 only so last week's overnight low is available. Each block keeps at most **224** newest rows, so last week is not dropped to keep newer recent rows. The recent block still fits a 15-minute cadence. That overnight low is among the newest rows of the prior block, so it stays inside that cap. A recent row is required. If last week's points are missing, the forecast is still returned and the extra prompt blocks are left out. When the points exist, the prompt labels that hourly emptying curve (weekday, date, hour, level, capacity) and last week at this clock time through the next 6 hours, and tells the model to use the curve for the evening drop: when it starts, how fast it falls, how much occupancy remains after it bottoms out, not to predict 0 where last week still had residual cars, and not to smooth the drop away. Prompt times are Asia/Hong_Kong local time, with the weekday. When the latest 2–4 quarter-hour snapshots are a steep climb or drop (at least 15% of capacity per hour), the prompt states that slope and the exact hour 1–6 levels, and the forecast is set to those levels, still capped at 0 and capacity. A flat or gentle recent series is left unchanged. After the model forecast and that slope overwrite, each hour is raised to last week's same-weekday overnight minimum actual level (17:00 through 08:00 the next morning, Asia/Hong_Kong) when it would fall below that minimum, then capped at 0 and at capacity. The minimum is a leftover-cars floor, not last week's level at the same clock time. If the latest level is already below that minimum, the floor is the latest level, so the forecast is not lifted into a rebound. If no point in that overnight window was returned, the floor is skipped and the forecast is still returned. `occupancyPercent` is recomputed from the clamped level. No new tables.
  - `200` body:
    ```json
    {"carParkCode":"CP1","countingCategory":"Total","model":"gemini-3.5-flash-lite",
     "forecasts":[{"hourOffset":1,"currentLevel":41,"occupancyPercent":41.0}]}
    ```
    Six items, `hourOffset` 1–6. `occupancyPercent` is `currentLevel / latest Capacity * 100` (1 decimal), or `null` when that capacity is 0.
  - `400` bad input, or `countingCategory` is not `Total`.
  - `404` no Total `CarparkNo` `0` rows in the last 48 hours.
  - `502` Gemini HTTP/transport failure or an unreadable forecast. `503` when the Gemini API key is missing or `Vertex__Model` is not a plain model id.
- `POST /api/occupancy/official-hourly` — no body. Intended for a coordinator call at `:05` Asia/Hong_Kong. Uses the current Hong Kong clock hour truncated to the hour as `OfficialHourHk` (`DATETIMEOFFSET(0)` with offset `+08:00`, on the hour). For every open catalog park on the live latest board (`IsClosed = 0`, `countingCategory` exactly `Total`, `carparkNo` `0`), resolves that hour's official `:00` latest (from latest when its Hong Kong minute is `0`, else from history for the same hour; seconds may be slightly past the hour). Skips a park when official `:00` is missing. Upserts `dbo.OfficialHourlyOccupancy` on `(OfficialHourHk, CarParkCode)` with `SnapshotTime`, `CurrentLevel`, `Capacity`, `OccupancyPercent`, and that official `:00` board's `+1` hour forecast (`ForecastLevel`, `ForecastPercent`) via the existing in-process predict path (tip forced to official `:00`, even if live latest has rolled mid-hour). Predict failure still upserts the official latest with null forecast fields. `200` summary:
  ```json
  {"officialHourHk":"2026-11-18T09:00:00+08:00","upserted":10,"skippedMissingOfficial":1,"forecastFailed":0}
  ```
  Table script: `db/011_OfficialHourlyOccupancy.sql` (already applied live; do not re-apply unless provisioning a new DB).

`dotnet test` stubs history and Gemini and sends a test `X-Api-Key`. It does not call the live API and does not need `ApiKey` or `Gemini__ApiKey` in the environment. A live check is a request with `X-Api-Key` against a running API that has `ApiKey` and `Gemini__ApiKey` (or `GEMINI_API_KEY` / `Vertex__ApiKey`) set and rows already in `dbo.OccupancySnapshot`.
