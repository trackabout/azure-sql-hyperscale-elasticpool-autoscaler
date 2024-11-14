-- Create the hs schema if it does not exist
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'hs')
BEGIN
	EXEC('CREATE SCHEMA [hs]')
END

DROP TABLE IF EXISTS [hs].[Numbers]
GO

SELECT TOP (1000000)
	ROW_NUMBER() OVER (ORDER BY A.[object_id]) AS Number,
	RAND(CHECKSUM(NEWID())) AS Random
INTO
	[hs].[Numbers]
FROM
	sys.[all_columns] a, sys.[all_columns] b
GO

CREATE CLUSTERED INDEX ixc ON [hs].[Numbers](Number)
GO