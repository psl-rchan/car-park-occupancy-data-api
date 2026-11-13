-- Car-park master catalog for HK region and 18-district classification.
-- Snapshot and history tables are intentionally unchanged.
-- Region and District are nullable so unknown parks can be filled later.

IF OBJECT_ID(N'dbo.CarPark', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CarPark (
        CarParkCode NVARCHAR(50) NOT NULL CONSTRAINT PK_CarPark PRIMARY KEY,
        Region      NVARCHAR(10) NULL,
        District    NVARCHAR(20) NULL,
        CONSTRAINT CK_CarPark_Region CHECK (
            Region IS NULL OR Region IN (N'香港島', N'九龍', N'新界')
        ),
        CONSTRAINT CK_CarPark_District CHECK (
            District IS NULL OR District IN (
                N'中西區', N'灣仔區', N'東區', N'南區',
                N'油尖旺區', N'深水埗區', N'九龍城區', N'黃大仙區', N'觀塘區',
                N'荃灣區', N'屯門區', N'元朗區', N'北區', N'大埔區', N'沙田區', N'西貢區', N'葵青區', N'離島區'
            )
        ),
        CONSTRAINT CK_CarPark_RegionDistrict CHECK (
            Region IS NULL
            OR District IS NULL
            OR (Region = N'香港島' AND District IN (N'中西區', N'灣仔區', N'東區', N'南區'))
            OR (Region = N'九龍' AND District IN (N'油尖旺區', N'深水埗區', N'九龍城區', N'黃大仙區', N'觀塘區'))
            OR (Region = N'新界' AND District IN (N'荃灣區', N'屯門區', N'元朗區', N'北區', N'大埔區', N'沙田區', N'西貢區', N'葵青區', N'離島區'))
        )
    );
END
GO

INSERT INTO dbo.CarPark (CarParkCode)
SELECT DISTINCT s.CarParkCode
FROM dbo.OccupancySnapshot AS s
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.CarPark AS c
    WHERE c.CarParkCode = s.CarParkCode
);
GO

MERGE dbo.CarPark AS target
USING (
    VALUES
        (N'P173', N'新界', N'荃灣區'),
        (N'P617', N'新界', N'荃灣區'),
        (N'P648', N'香港島', N'中西區'),
        (N'P736', N'九龍', N'深水埗區'),
        (N'P821', N'九龍', N'油尖旺區'),
        (N'P909', N'九龍', N'深水埗區')
) AS source (CarParkCode, Region, District)
ON target.CarParkCode = source.CarParkCode
WHEN MATCHED AND target.Region IS NULL AND target.District IS NULL THEN
    UPDATE SET
        Region = source.Region,
        District = source.District
WHEN NOT MATCHED THEN
    INSERT (CarParkCode, Region, District)
    VALUES (source.CarParkCode, source.Region, source.District);
GO
