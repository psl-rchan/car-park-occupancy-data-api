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
            N'Central and Western', N'Wan Chai', N'Eastern', N'Southern',
            N'Yau Tsim Mong', N'Sham Shui Po', N'Kowloon City', N'Wong Tai Sin', N'Kwun Tong',
            N'Tsuen Wan', N'Tuen Mun', N'Yuen Long', N'North', N'Tai Po',
            N'Sha Tin', N'Sai Kung', N'Kwai Tsing', N'Islands'
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
            (District = N'中西區' AND DistrictEn = N'Central and Western') OR
            (District = N'灣仔區' AND DistrictEn = N'Wan Chai') OR
            (District = N'東區' AND DistrictEn = N'Eastern') OR
            (District = N'南區' AND DistrictEn = N'Southern') OR
            (District = N'油尖旺區' AND DistrictEn = N'Yau Tsim Mong') OR
            (District = N'深水埗區' AND DistrictEn = N'Sham Shui Po') OR
            (District = N'九龍城區' AND DistrictEn = N'Kowloon City') OR
            (District = N'黃大仙區' AND DistrictEn = N'Wong Tai Sin') OR
            (District = N'觀塘區' AND DistrictEn = N'Kwun Tong') OR
            (District = N'荃灣區' AND DistrictEn = N'Tsuen Wan') OR
            (District = N'屯門區' AND DistrictEn = N'Tuen Mun') OR
            (District = N'元朗區' AND DistrictEn = N'Yuen Long') OR
            (District = N'北區' AND DistrictEn = N'North') OR
            (District = N'大埔區' AND DistrictEn = N'Tai Po') OR
            (District = N'沙田區' AND DistrictEn = N'Sha Tin') OR
            (District = N'西貢區' AND DistrictEn = N'Sai Kung') OR
            (District = N'葵青區' AND DistrictEn = N'Kwai Tsing') OR
            (District = N'離島區' AND DistrictEn = N'Islands')
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
        OR (RegionEn = N'Hong Kong Island' AND DistrictEn IN (N'Central and Western', N'Wan Chai', N'Eastern', N'Southern'))
        OR (RegionEn = N'Kowloon' AND DistrictEn IN (N'Yau Tsim Mong', N'Sham Shui Po', N'Kowloon City', N'Wong Tai Sin', N'Kwun Tong'))
        OR (RegionEn = N'New Territories' AND DistrictEn IN (N'Tsuen Wan', N'Tuen Mun', N'Yuen Long', N'North', N'Tai Po', N'Sha Tin', N'Sai Kung', N'Kwai Tsing', N'Islands'))
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
        WHEN N'中西區' THEN N'Central and Western'
        WHEN N'灣仔區' THEN N'Wan Chai'
        WHEN N'東區' THEN N'Eastern'
        WHEN N'南區' THEN N'Southern'
        WHEN N'油尖旺區' THEN N'Yau Tsim Mong'
        WHEN N'深水埗區' THEN N'Sham Shui Po'
        WHEN N'九龍城區' THEN N'Kowloon City'
        WHEN N'黃大仙區' THEN N'Wong Tai Sin'
        WHEN N'觀塘區' THEN N'Kwun Tong'
        WHEN N'荃灣區' THEN N'Tsuen Wan'
        WHEN N'屯門區' THEN N'Tuen Mun'
        WHEN N'元朗區' THEN N'Yuen Long'
        WHEN N'北區' THEN N'North'
        WHEN N'大埔區' THEN N'Tai Po'
        WHEN N'沙田區' THEN N'Sha Tin'
        WHEN N'西貢區' THEN N'Sai Kung'
        WHEN N'葵青區' THEN N'Kwai Tsing'
        WHEN N'離島區' THEN N'Islands'
    END
WHERE Region IS NOT NULL
  AND District IS NOT NULL
  AND RegionEn IS NULL
  AND DistrictEn IS NULL;
GO
