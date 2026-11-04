-- Covering index for GET /api/occupancy/history on dbo.OccupancySnapshot.
-- History: equality on CarParkCode, CountingCategory, and CarparkNo, plus a SnapshotTime range.
--
-- (CarParkCode, CountingCategory, CarparkNo, SnapshotTime) supports that equality + range.
-- SnapshotTime is ASC to match ORDER BY SnapshotTime ASC. Do not use DESC here.
-- INCLUDE covers the remaining selected columns so the history read can be index-only.
-- IX_OccupancySnapshot_CarParkCode_SnapshotTime remains for latest and predict.

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_OccupancySnapshot_History'
      AND object_id = OBJECT_ID(N'dbo.OccupancySnapshot')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_OccupancySnapshot_History
        ON dbo.OccupancySnapshot (CarParkCode, CountingCategory, CarparkNo, SnapshotTime)
        INCLUDE (CarparkDesig, CountingCategoryNo, FreeLimit, OccupancyLimit, CurrentLevel, Capacity);
END
