-- This script adds the Notes column to the hs.AutoScalerMonitor table if it doesn't already exist
-- This is needed for supporting the geo-replication delay feature that was added in June 2025
IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('hs.AutoScalerMonitor')
    AND name = 'Notes'
)
BEGIN
    ALTER TABLE [hs].[AutoScalerMonitor]
    ADD [Notes] NVARCHAR(1000) NULL;

    PRINT 'Notes column added to hs.AutoScalerMonitor table.';
END
ELSE
BEGIN
    PRINT 'Notes column already exists in hs.AutoScalerMonitor table. No changes made.';
END
GO
