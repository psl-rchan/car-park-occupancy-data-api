-- Rename dbo.CarPark Region/RegionEn to Area/AreaEn and add nullable SubDistrict/SubDistrictEn.
-- Values are unchanged: 香港島 / 九龍 / 新界 and Hong Kong Island / Kowloon / New Territories.
-- District / DistrictEn are unchanged (Chinese keeps 區; English keeps the word District).
-- SubDistrict columns are left NULL; no 分區 seed values are invented.
-- Does not alter dbo.OccupancySnapshot.
-- Idempotent.

-- Drop Area-related CHECKs under either the old or new name so columns can be renamed safely.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_Region' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_Region;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_Area' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_Area;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionDistrict' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_RegionDistrict;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_AreaDistrict' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_AreaDistrict;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEn' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_RegionEn;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_AreaEn' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_AreaEn;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEn_Match' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_RegionEn_Match;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_AreaEn_Match' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_AreaEn_Match;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEnDistrictEn' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_RegionEnDistrictEn;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_AreaEnDistrictEn' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_AreaEnDistrictEn;
GO

IF COL_LENGTH(N'dbo.CarPark', N'Region') IS NOT NULL
   AND COL_LENGTH(N'dbo.CarPark', N'Area') IS NULL
    EXEC sp_rename N'dbo.CarPark.Region', N'Area', N'COLUMN';
GO

IF COL_LENGTH(N'dbo.CarPark', N'RegionEn') IS NOT NULL
   AND COL_LENGTH(N'dbo.CarPark', N'AreaEn') IS NULL
    EXEC sp_rename N'dbo.CarPark.RegionEn', N'AreaEn', N'COLUMN';
GO

-- Rename any leftover index that still uses the old column name in its name.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_CarPark_Region' AND object_id = OBJECT_ID(N'dbo.CarPark')
)
    EXEC sp_rename N'dbo.CarPark.IX_CarPark_Region', N'IX_CarPark_Area', N'INDEX';
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_CarPark_RegionEn' AND object_id = OBJECT_ID(N'dbo.CarPark')
)
    EXEC sp_rename N'dbo.CarPark.IX_CarPark_RegionEn', N'IX_CarPark_AreaEn', N'INDEX';
GO

IF COL_LENGTH(N'dbo.CarPark', N'Area') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_Area' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_Area CHECK (
        Area IS NULL OR Area IN (N'香港島', N'九龍', N'新界')
    );
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'Area') IS NOT NULL
   AND COL_LENGTH(N'dbo.CarPark', N'District') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_AreaDistrict' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_AreaDistrict CHECK (
        Area IS NULL
        OR District IS NULL
        OR (Area = N'香港島' AND District IN (N'中西區', N'灣仔區', N'東區', N'南區'))
        OR (Area = N'九龍' AND District IN (N'油尖旺區', N'深水埗區', N'九龍城區', N'黃大仙區', N'觀塘區'))
        OR (Area = N'新界' AND District IN (N'荃灣區', N'屯門區', N'元朗區', N'北區', N'大埔區', N'沙田區', N'西貢區', N'葵青區', N'離島區'))
    );
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'AreaEn') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_AreaEn' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_AreaEn CHECK (
        AreaEn IS NULL OR AreaEn IN (N'Hong Kong Island', N'Kowloon', N'New Territories')
    );
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'AreaEn') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_AreaEn_Match' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_AreaEn_Match CHECK (
        AreaEn IS NULL OR (
            (Area = N'香港島' AND AreaEn = N'Hong Kong Island') OR
            (Area = N'九龍' AND AreaEn = N'Kowloon') OR
            (Area = N'新界' AND AreaEn = N'New Territories')
        )
    );
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'AreaEn') IS NOT NULL
   AND COL_LENGTH(N'dbo.CarPark', N'DistrictEn') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_AreaEnDistrictEn' AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_AreaEnDistrictEn CHECK (
        AreaEn IS NULL
        OR DistrictEn IS NULL
        OR (AreaEn = N'Hong Kong Island' AND DistrictEn IN (N'Central and Western District', N'Wan Chai District', N'Eastern District', N'Southern District'))
        OR (AreaEn = N'Kowloon' AND DistrictEn IN (N'Yau Tsim Mong District', N'Sham Shui Po District', N'Kowloon City District', N'Wong Tai Sin District', N'Kwun Tong District'))
        OR (AreaEn = N'New Territories' AND DistrictEn IN (N'Tsuen Wan District', N'Tuen Mun District', N'Yuen Long District', N'North District', N'Tai Po District', N'Sha Tin District', N'Sai Kung District', N'Kwai Tsing District', N'Islands District'))
    );
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'SubDistrict') IS NULL
   AND COL_LENGTH(N'dbo.CarPark', N'SubDistrictEn') IS NULL
BEGIN
    ALTER TABLE dbo.CarPark ADD
        SubDistrict   NVARCHAR(40) NULL,
        SubDistrictEn NVARCHAR(40) NULL;
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'SubDistrict') IS NOT NULL
   AND COL_LENGTH(N'dbo.CarPark', N'SubDistrictEn') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_SubDistrictEn_RequiresSubDistrict'
          AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_SubDistrictEn_RequiresSubDistrict CHECK (
        SubDistrictEn IS NULL OR SubDistrict IS NOT NULL
    );
END
GO

IF COL_LENGTH(N'dbo.CarPark', N'SubDistrict') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_CarPark_SubDistrict_RequiresAreaDistrict'
          AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
   )
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_SubDistrict_RequiresAreaDistrict CHECK (
        SubDistrict IS NULL OR (Area IS NOT NULL AND District IS NOT NULL)
    );
END
GO
