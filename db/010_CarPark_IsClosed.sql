-- 010_CarPark_IsClosed.sql
-- Add IsClosed (BIT, NOT NULL) to dbo.CarPark, default 0.
-- Existing rows are filled with 0 by the default constraint, so every
-- current park stays visible. No park is marked closed.
-- Existing constraints are left unchanged, including CK_CarPark_NameEn_RequiresName.
-- OccupancySnapshot is unchanged. No catalog value updates. No INSERTs.
-- Idempotent: safe to re-run. Do not apply until explicitly authorized.

IF OBJECT_ID(N'dbo.CarPark', N'U') IS NULL
BEGIN
    RAISERROR(N'dbo.CarPark does not exist; run db/004_CarPark.sql first.', 16, 1);
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'IsClosed') IS NULL
BEGIN
    ALTER TABLE dbo.CarPark
        ADD IsClosed BIT NOT NULL
            CONSTRAINT DF_CarPark_IsClosed DEFAULT (0);
END
GO
