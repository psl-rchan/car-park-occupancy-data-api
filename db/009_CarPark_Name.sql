-- Add nullable Name and NameEn to dbo.CarPark.
-- Columns are left NULL; no park-name seed values are invented.
-- OccupancySnapshot is unchanged.
-- Idempotent: safe to re-run.

IF OBJECT_ID(N'dbo.CarPark', N'U') IS NULL
BEGIN
    RAISERROR(N'dbo.CarPark does not exist; run db/004_CarPark.sql first.', 16, 1);
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'Name') IS NULL
BEGIN
    ALTER TABLE dbo.CarPark ADD Name NVARCHAR(200) NULL;
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'NameEn') IS NULL
BEGIN
    ALTER TABLE dbo.CarPark ADD NameEn NVARCHAR(200) NULL;
END
GO

-- Light CHECK only: a filled NameEn requires Name. No official-name list.
IF COL_LENGTH(N'dbo.CarPark', N'Name') IS NOT NULL
   AND COL_LENGTH(N'dbo.CarPark', N'NameEn') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_CarPark_NameEn_RequiresName'
          AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_NameEn_RequiresName CHECK (
        NameEn IS NULL OR Name IS NOT NULL
    );
END
GO
