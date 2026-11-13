-- Restore the word District on dbo.CarPark English district names.
-- RegionEn values (Hong Kong Island, Kowloon, New Territories) are unchanged.
-- Chinese District values (including 區) are unchanged.
-- Does not alter dbo.OccupancySnapshot.
-- Idempotent: safe if names already end with ' District' or constraints were already replaced.

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_DistrictEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_DistrictEn;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_DistrictEn_Match'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_DistrictEn_Match;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_RegionEnDistrictEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
    ALTER TABLE dbo.CarPark DROP CONSTRAINT CK_CarPark_RegionEnDistrictEn;
GO

UPDATE dbo.CarPark
SET DistrictEn = DistrictEn + N' District'
WHERE DistrictEn IS NOT NULL
  AND DistrictEn NOT LIKE N'% District';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_DistrictEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_DistrictEn CHECK (
        DistrictEn IS NULL OR DistrictEn IN (
            N'Central and Western District', N'Wan Chai District', N'Eastern District', N'Southern District', N'Yau Tsim Mong District', N'Sham Shui Po District', N'Kowloon City District', N'Wong Tai Sin District', N'Kwun Tong District', N'Tsuen Wan District', N'Tuen Mun District', N'Yuen Long District', N'North District', N'Tai Po District', N'Sha Tin District', N'Sai Kung District', N'Kwai Tsing District', N'Islands District'
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
            (District = N'中西區' AND DistrictEn = N'Central and Western District')
            OR (District = N'灣仔區' AND DistrictEn = N'Wan Chai District')
            OR (District = N'東區' AND DistrictEn = N'Eastern District')
            OR (District = N'南區' AND DistrictEn = N'Southern District')
            OR (District = N'油尖旺區' AND DistrictEn = N'Yau Tsim Mong District')
            OR (District = N'深水埗區' AND DistrictEn = N'Sham Shui Po District')
            OR (District = N'九龍城區' AND DistrictEn = N'Kowloon City District')
            OR (District = N'黃大仙區' AND DistrictEn = N'Wong Tai Sin District')
            OR (District = N'觀塘區' AND DistrictEn = N'Kwun Tong District')
            OR (District = N'荃灣區' AND DistrictEn = N'Tsuen Wan District')
            OR (District = N'屯門區' AND DistrictEn = N'Tuen Mun District')
            OR (District = N'元朗區' AND DistrictEn = N'Yuen Long District')
            OR (District = N'北區' AND DistrictEn = N'North District')
            OR (District = N'大埔區' AND DistrictEn = N'Tai Po District')
            OR (District = N'沙田區' AND DistrictEn = N'Sha Tin District')
            OR (District = N'西貢區' AND DistrictEn = N'Sai Kung District')
            OR (District = N'葵青區' AND DistrictEn = N'Kwai Tsing District')
            OR (District = N'離島區' AND DistrictEn = N'Islands District')
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
