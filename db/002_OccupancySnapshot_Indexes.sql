-- Indexes for dbo.OccupancySnapshot reads.
-- Latest: PARTITION BY CarParkCode, CountingCategory ORDER BY SnapshotTime DESC, Id DESC.
-- Predict: CarParkCode equality, optional CountingCategory equality, SnapshotTime window.
--
-- (CarParkCode, SnapshotTime DESC) supports both. Id is already the clustered key,
-- so it is present at the leaf for the latest tie-break.
-- Not created: a SnapshotTime-only index (every predict predicate includes CarParkCode),
-- and (CarParkCode, CountingCategoryNo, SnapshotTime DESC) (queries use CountingCategory, not the number).

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_OccupancySnapshot_CarParkCode_SnapshotTime'
      AND object_id = OBJECT_ID(N'dbo.OccupancySnapshot')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_OccupancySnapshot_CarParkCode_SnapshotTime
        ON dbo.OccupancySnapshot (CarParkCode, SnapshotTime DESC);
END
