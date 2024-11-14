-- Create the hs schema if it does not exist
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'hs')
BEGIN
	EXEC('CREATE SCHEMA [hs]')
END

DROP TABLE IF EXISTS [hs].[AutoScalerMonitor]
GO

CREATE TABLE [hs].[AutoScalerMonitor]
(
	[Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY CLUSTERED,
	[ElasticPoolName] NVARCHAR(100) NOT NULL,
	[InsertedAt] DATETIME2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),
	[CurrentSLO] DECIMAL(10,2) NOT NULL,
	[RequestedSLO] DECIMAL(10,2) NOT NULL,
	[UsageInfo] NVARCHAR(MAX) NULL CHECK(ISJSON([UsageInfo])=1)
)
GO
