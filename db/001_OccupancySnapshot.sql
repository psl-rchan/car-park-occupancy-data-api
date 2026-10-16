CREATE TABLE dbo.OccupancySnapshot (
    Id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OccupancySnapshot PRIMARY KEY,
    CarParkCode         NVARCHAR(50)   NOT NULL,
    SnapshotTime        DATETIMEOFFSET NOT NULL,
    CarparkNo           INT            NOT NULL,
    CarparkDesig        NVARCHAR(100)  NOT NULL,
    CountingCategoryNo  INT            NOT NULL,
    CountingCategory    NVARCHAR(100)  NOT NULL,
    FreeLimit           INT            NOT NULL,
    OccupancyLimit      INT            NOT NULL,
    CurrentLevel        INT            NOT NULL,
    Capacity            INT            NOT NULL
);
