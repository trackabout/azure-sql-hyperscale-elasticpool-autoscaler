namespace Azure.HyperScale.ElasticPool.AutoScaler;

public interface IAutoScalerConfiguration
{
    string PoolDbConnection { get; }
    string MetricsSqlConnection { get; }
    string MasterSqlConnection { get; }
    string SubscriptionId { get; }
    string SqlInstanceName { get; }
    string ResourceGroupName { get; }
    Dictionary<string, double?> ElasticPools { get; }
    decimal LowCpuPercent { get; }
    decimal HighCpuPercent { get; }
    decimal LowWorkersPercent { get; }
    decimal HighWorkersPercent { get; }
    decimal LowInstanceCpuPercent { get; }
    decimal HighInstanceCpuPercent { get; }
    decimal LowDataIoPercent { get; }
    decimal HighDataIoPercent { get; }
    int LongWindowLookback { get; }
    int ShortWindowLookback { get; }
    double VCoreFloor { get; }
    double VCoreCeiling { get; }
    List<double> VCoreOptions { get; }
    List<double> PerDatabaseMaximums { get; }
    bool IsSentryLoggingEnabled { get; }
    string SentryDsn { get; }
    int RetryCount { get; }
    int RetryInterval { get; }
    bool IsDryRun { get; }
    int MaxExpectedScalingTimeSeconds { get; }
    int CoolDownPeriodSeconds { get; }
    int ScaleUpSteps { get; }

    double GetVCoreFloorForPool(string poolName);
    double GetPerDatabaseMaxByVCore(double targetVCore);
}