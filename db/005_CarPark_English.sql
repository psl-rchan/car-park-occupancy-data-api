-- English (中英對照) columns for dbo.CarPark region and district.
-- Does not alter dbo.OccupancySnapshot.
-- RegionEn / DistrictEn stay nullable so unmapped parks remain empty
-- until the Chinese Region / District values are filled.

IF COL_LENGTH(N'dbo.CarPark', N'RegionEn') IS NULL
   AND COL_LENGTH(N'dbo.CarPark', N'DistrictEn') IS NULL
BEGIN
    ALTER TABLE dbo.CarPark ADD
        RegionEn   NVARCHAR(40) NULL,
        DistrictEn NVARCHAR(40) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_RegionEn CHECK (
        RegionEn IS NULL OR RegionEn IN (N'Hong Kong Island', N'Kowloon', N'New Territories')
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_DistrictEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_DistrictEn CHECK (
        DistrictEn IS NULL OR DistrictEn IN (
            N'Central and Western District', N'Wan Chai District', N'Eastern District', N'Southern District',
            N'Yau Tsim Mong District', N'Sham Shui Po District', N'Kowloon City District', N'Wong Tai Sin District', N'Kwun Tong District',
            N'Tsuen Wan District', N'Tuen Mun District', N'Yuen Long District', N'North District', N'Tai Po District',
            N'Sha Tin District', N'Sai Kung District', N'Kwai Tsing District', N'Islands District'
        )
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEn_Match'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_RegionEn_Match CHECK (
        RegionEn IS NULL OR (
            (Region = N'香港島' AND RegionEn = N'Hong Kong Island') OR
            (Region = N'九龍' AND RegionEn = N'Kowloon') OR
            (Region = N'新界' AND RegionEn = N'New Territories')
        )
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_DistrictEn_Match'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_DistrictEn_Match CHECK (
        DistrictEn IS NULL OR (
            (District = N'中西區' AND DistrictEn = N'Central and Western District') OR
            (District = N'灣仔區' AND DistrictEn = N'Wan Chai District') OR
            (District = N'東區' AND DistrictEn = N'Eastern District') OR
            (District = N'南區' AND DistrictEn = N'Southern District') OR
            (District = N'油尖旺區' AND DistrictEn = N'Yau Tsim Mong District') OR
            (District = N'深水埗區' AND DistrictEn = N'Sham Shui Po District') OR
            (District = N'九龍城區' AND DistrictEn = N'Kowloon City District') OR
            (District = N'黃大仙區' AND DistrictEn = N'Wong Tai Sin District') OR
            (District = N'觀塘區' AND DistrictEn = N'Kwun Tong District') OR
            (District = N'荃灣區' AND DistrictEn = N'Tsuen Wan District') OR
            (District = N'屯門區' AND DistrictEn = N'Tuen Mun District') OR
            (District = N'元朗區' AND DistrictEn = N'Yuen Long District') OR
            (District = N'北區' AND DistrictEn = N'North District') OR
            (District = N'大埔區' AND DistrictEn = N'Tai Po District') OR
            (District = N'沙田區' AND DistrictEn = N'Sha Tin District') OR
            (District = N'西貢區' AND DistrictEn = N'Sai Kung District') OR
            (District = N'葵青區' AND DistrictEn = N'Kwai Tsing District') OR
            (District = N'離島區' AND DistrictEn = N'Islands District')
        )
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEnDistrictEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_RegionEnDistrictEn CHECK (
        RegionEn IS NULL
        OR DistrictEn IS NULL
        OR (RegionEn = N'Hong Kong Island' AND DistrictEn IN (N'Central and Western District', N'Wan Chai District', N'Eastern District', N'Southern District'))
        OR (RegionEn = N'Kowloon' AND DistrictEn IN (N'Yau Tsim Mong District', N'Sham Shui Po District', N'Kowloon City District', N'Wong Tai Sin District', N'Kwun Tong District'))
        OR (RegionEn = N'New Territories' AND DistrictEn IN (N'Tsuen Wan District', N'Tuen Mun District', N'Yuen Long District', N'North District', N'Tai Po District', N'Sha Tin District', N'Sai Kung District', N'Kwai Tsing District', N'Islands District'))
    );
END
GO

-- Fill English only where Chinese Region and District are already set
-- and English is still empty. Leaves unmapped parks empty.
UPDATE dbo.CarPark
SET
    RegionEn = CASE Region
        WHEN N'香港島' THEN N'Hong Kong Island'
        WHEN N'九龍' THEN N'Kowloon'
        WHEN N'新界' THEN N'New Territories'
    END,
    DistrictEn = CASE District
        WHEN N'中西區' THEN N'Central and Western District'
        WHEN N'灣仔區' THEN N'Wan Chai District'
        WHEN N'東區' THEN N'Eastern District'
        WHEN N'南區' THEN N'Southern District'
        WHEN N'油尖旺區' THEN N'Yau Tsim Mong District'
        WHEN N'深水埗區' THEN N'Sham Shui Po District'
        WHEN N'九龍城區' THEN N'Kowloon City District'
        WHEN N'黃大仙區' THEN N'Wong Tai Sin District'
        WHEN N'觀塘區' THEN N'Kwun Tong District'
        WHEN N'荃灣區' THEN N'Tsuen Wan District'
        WHEN N'屯門區' THEN N'Tuen Mun District'
        WHEN N'元朗區' THEN N'Yuen Long District'
        WHEN N'北區' THEN N'North District'
        WHEN N'大埔區' THEN N'Tai Po District'
        WHEN N'沙田區' THEN N'Sha Tin District'
        WHEN N'西貢區' THEN N'Sai Kung District'
        WHEN N'葵青區' THEN N'Kwai Tsing District'
        WHEN N'離島區' THEN N'Islands District'
    END
WHERE Region IS NOT NULL
  AND District IS NOT NULL
  AND RegionEn IS NULL
  AND DistrictEn IS NULL;
GO
