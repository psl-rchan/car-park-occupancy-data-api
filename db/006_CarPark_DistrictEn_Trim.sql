-- Trim the word District from dbo.CarPark English district names.
-- RegionEn values (Hong Kong Island, Kowloon, New Territories) are unchanged.
-- Does not alter dbo.OccupancySnapshot.
-- Idempotent: safe if names are already trimmed or constraints were already replaced.

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
SET DistrictEn = LEFT(DistrictEn, LEN(DistrictEn) - LEN(N' District'))
WHERE DistrictEn LIKE N'% District';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_CarPark_DistrictEn'
      AND parent_object_id = OBJECT_ID(N'dbo.CarPark')
)
BEGIN
    ALTER TABLE dbo.CarPark ADD CONSTRAINT CK_CarPark_DistrictEn CHECK (
        DistrictEn IS NULL OR DistrictEn IN (
            N'Central and Western', N'Wan Chai', N'Eastern', N'Southern', N'Yau Tsim Mong', N'Sham Shui Po', N'Kowloon City', N'Wong Tai Sin', N'Kwun Tong', N'Tsuen Wan', N'Tuen Mun', N'Yuen Long', N'North', N'Tai Po', N'Sha Tin', N'Sai Kung', N'Kwai Tsing', N'Islands'
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
            (District = N'中西區' AND DistrictEn = N'Central and Western')
            OR (District = N'灣仔區' AND DistrictEn = N'Wan Chai')
            OR (District = N'東區' AND DistrictEn = N'Eastern')
            OR (District = N'南區' AND DistrictEn = N'Southern')
            OR (District = N'油尖旺區' AND DistrictEn = N'Yau Tsim Mong')
            OR (District = N'深水埗區' AND DistrictEn = N'Sham Shui Po')
            OR (District = N'九龍城區' AND DistrictEn = N'Kowloon City')
            OR (District = N'黃大仙區' AND DistrictEn = N'Wong Tai Sin')
            OR (District = N'觀塘區' AND DistrictEn = N'Kwun Tong')
            OR (District = N'荃灣區' AND DistrictEn = N'Tsuen Wan')
            OR (District = N'屯門區' AND DistrictEn = N'Tuen Mun')
            OR (District = N'元朗區' AND DistrictEn = N'Yuen Long')
            OR (District = N'北區' AND DistrictEn = N'North')
            OR (District = N'大埔區' AND DistrictEn = N'Tai Po')
            OR (District = N'沙田區' AND DistrictEn = N'Sha Tin')
            OR (District = N'西貢區' AND DistrictEn = N'Sai Kung')
            OR (District = N'葵青區' AND DistrictEn = N'Kwai Tsing')
            OR (District = N'離島區' AND DistrictEn = N'Islands')
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
