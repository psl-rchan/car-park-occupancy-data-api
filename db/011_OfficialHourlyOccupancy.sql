-- 011_OfficialHourlyOccupancy.sql
-- Additive table dbo.OfficialHourlyOccupancy.
-- One row per catalog park per official :00 Asia/Hong_Kong board.
-- Stores the official latest (SnapshotTime, CurrentLevel, Capacity,
-- OccupancyPercent) and that board's official +1 hour forecast only
-- (ForecastLevel, ForecastPercent). Source for the daily noon +1h
-- accuracy report.
-- Grain is the live latest board for open catalog parks,
-- countingCategory Total, carparkNo 0. Those constants are not stored.
-- Mid-hour :15/:30/:45 points must not be stored as official :00:
-- OfficialHourHk is constrained to offset +08:00 and exactly on the hour.
-- Idempotent upsert key: (OfficialHourHk, CarParkCode).
-- Does not alter dbo.CarPark or dbo.OccupancySnapshot.
-- Does not INSERT catalog rows or seed values.
-- Idempotent: safe to re-run.

IF OBJECT_ID(N'dbo.CarPark', N'U') IS NULL
BEGIN
    RAISERROR(N'dbo.CarPark does not exist; run db/004_CarPark.sql first.', 16, 1);
    RETURN;
END
GO

IF OBJECT_ID(N'dbo.OfficialHourlyOccupancy', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OfficialHourlyOccupancy
    (
        OfficialHourHk   DATETIMEOFFSET(0) NOT NULL,
        CarParkCode      NVARCHAR(20)      NOT NULL,
        SnapshotTime     DATETIMEOFFSET(7) NOT NULL,
        CurrentLevel     INT               NOT NULL,
        Capacity         INT               NOT NULL,
        OccupancyPercent DECIMAL(9, 2)     NULL,
        ForecastLevel    INT               NULL,
        ForecastPercent  DECIMAL(9, 2)     NULL,
        CONSTRAINT PK_OfficialHourlyOccupancy
            PRIMARY KEY CLUSTERED (OfficialHourHk, CarParkCode),
        CONSTRAINT FK_OfficialHourlyOccupancy_CarPark
            FOREIGN KEY (CarParkCode) REFERENCES dbo.CarPark (CarParkCode),
        CONSTRAINT CK_OfficialHourlyOccupancy_OfficialHourHk_OnTheHour
            CHECK (
                DATEPART(TZOFFSET, OfficialHourHk) = 480
                AND DATEPART(MINUTE, OfficialHourHk) = 0
                AND DATEPART(SECOND, OfficialHourHk) = 0
                AND DATEPART(MILLISECOND, OfficialHourHk) = 0
            ),
        CONSTRAINT CK_OfficialHourlyOccupancy_ForecastPercent_RequiresLevel
            CHECK (ForecastPercent IS NULL OR ForecastLevel IS NOT NULL)
    );
END
GO
