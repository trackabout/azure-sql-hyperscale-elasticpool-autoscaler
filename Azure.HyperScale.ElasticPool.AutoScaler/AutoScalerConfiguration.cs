using Microsoft.Extensions.Configuration;

namespace Azure.HyperScale.ElasticPool.AutoScaler;

public class AutoScalerConfiguration
{
    public string PoolDbConnection { get; }
    public string MetricsSqlConnection { get; }
    public string MasterSqlConnection { get; }
    public string SubscriptionId { get; }
    public string SqlInstanceName { get; }
    public string ResourceGroupName { get; }
    public Dictionary<string, double?> ElasticPools { get; set; }
    public decimal LowCpuPercent { get; set;}
    public decimal HighCpuPercent { get; set;}
    public decimal LowWorkersPercent { get; set;}
    public decimal HighWorkersPercent { get; set; }
    public decimal LowInstanceCpuPercent { get; set; }
    public decimal HighInstanceCpuPercent { get; set;}
    public decimal LowDataIoPercent { get; set; }
    public decimal HighDataIoPercent { get; set; }
    public int LongWindowLookback { get; }
    public int ShortWindowLookback { get; }
    public double VCoreFloor { get; set; }
    public double VCoreCeiling { get; set; }
    public List<double> VCoreOptions { get; }
    public List<double> PerDatabaseMaximums { get; }
    public bool IsSentryLoggingEnabled { get; }
    public string SentryDsn { get; }
    public int RetryCount { get; }
    public int RetryInterval { get; }
    public bool IsDryRun { get; set; }
    public int MaxExpectedScalingTimeSeconds { get; }
    public int CoolDownPeriodSeconds { get; set; }
    public static bool IsUsingManagedIdentity => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"));
    public static string ManagedIdentityClientId => Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? string.Empty;

    public AutoScalerConfiguration(IConfiguration configuration)
    {
        PoolDbConnection = configuration.GetValue<string>("ConnectionStrings:PoolDbConnection") ?? throw new InvalidOperationException("PoolDbConnection is not set.");
        MetricsSqlConnection = configuration.GetValue<string>("ConnectionStrings:MetricsSqlConnection") ?? "";
        MasterSqlConnection = configuration.GetValue<string>("ConnectionStrings:MasterSqlConnection") ?? throw new InvalidOperationException("MasterSqlConnection is not set.");
        SubscriptionId = configuration.GetValue<string>("SubscriptionId") ?? throw new InvalidOperationException("SubscriptionId is not set.");
        SqlInstanceName = configuration.GetValue<string>("SqlInstanceName") ?? throw new InvalidOperationException("SqlInstanceName is not set.");
        ResourceGroupName = configuration.GetValue<string>("ResourceGroupName") ?? throw new InvalidOperationException("ResourceGroupName is not set.");


        LowCpuPercent = configuration.GetValue<decimal>("LowCpuPercent");
        HighCpuPercent = configuration.GetValue<decimal>("HighCpuPercent");

        LowWorkersPercent = configuration.GetValue<decimal>("LowWorkersPercent");
        HighWorkersPercent = configuration.GetValue<decimal>("HighWorkersPercent");

        LowInstanceCpuPercent = configuration.GetValue<decimal>("LowInstanceCpuPercent");
        HighInstanceCpuPercent = configuration.GetValue<decimal>("HighInstanceCpuPercent");

        LongWindowLookback = configuration.GetValue<int>("LongWindowLookback", 900);
        ShortWindowLookback = configuration.GetValue<int>("ShortWindowLookback", 300);

        VCoreFloor = configuration.GetValue<double>("VCoreFloor");
        VCoreCeiling = configuration.GetValue<double>("VCoreCeiling");

        VCoreOptions = ParseVCoreList(configuration.GetValue<string>("VCoreOptions") ?? throw new InvalidOperationException("VCoreOptions is not set."));
        PerDatabaseMaximums = ParseVCoreList(configuration.GetValue<string>("PerDatabaseMaximums") ?? throw new InvalidOperationException("PerDatabaseMaximums is not set."));

        HighDataIoPercent = configuration.GetValue<decimal>("HighDataIoPercent");
        LowDataIoPercent = configuration.GetValue<decimal>("LowDataIoPercent");

        RetryCount = configuration.GetValue<int>("RetryCount", 3);
        RetryInterval = configuration.GetValue<int>("RetryInterval", 2);

        IsDryRun = configuration.GetValue<bool>("IsDryRun");

        SentryDsn = configuration.GetValue<string>("SentryDsn") ?? string.Empty;

        IsSentryLoggingEnabled = !string.IsNullOrEmpty(SentryDsn);

        MaxExpectedScalingTimeSeconds = configuration.GetValue<int>("MaxExpectedScalingTimeSeconds");
        CoolDownPeriodSeconds = configuration.GetValue<int>("CoolDownPeriodSeconds");

        ElasticPools = configuration.GetValue<string>("ElasticPools")?
            .Split(',')
            .Select(p => p.Trim())
            .ToDictionary(
            p => p.Split(':')[0].Trim(),
            p =>
            {
                var value = p.Contains(':') ? double.Parse(p.Split(':')[1].Trim()) : (double?)null;
                if (value.HasValue && !VCoreOptions.Contains(value.Value))
                {
                    throw new InvalidOperationException($"Custom vCore floor {value.Value} for pool {p.Split(':')[0].Trim()} is not within the bounds of VCoreOptions.");
                }
                return value;
            }
            ) ?? throw new InvalidOperationException("ElasticPools is not set.");


        // There must be the same number of VCoreOptions as PerDatabaseMaximums
        if (VCoreOptions.Count != PerDatabaseMaximums.Count)
        {
            throw new InvalidOperationException("VCoreOptions and PerDatabaseMaximums must have the same number of elements.");
        }

        // Ensure that the VCoreFloor and VCoreCeiling are within the VCoreOptions range
        // Is VCoreFloor found in VCoreOptions?
        if (!VCoreOptions.Contains(VCoreFloor))
        {
            throw new InvalidOperationException("VCoreFloor must be found within VCoreOptions.");
        }

        if (!VCoreOptions.Contains(VCoreCeiling))
        {
            throw new InvalidOperationException("VCoreCeiling must be found within VCoreOptions.");
        }

        // Floor must be less than ceiling
        if (VCoreFloor >= VCoreCeiling)
        {
            throw new InvalidOperationException("VCoreFloor must be less than VCoreCeiling.");
        }

        // The various Low/High thresholds must make sense.
        if (LowCpuPercent >= HighCpuPercent || LowWorkersPercent >= HighWorkersPercent || LowInstanceCpuPercent >= HighInstanceCpuPercent || LowDataIoPercent >= HighDataIoPercent)
        {
            throw new InvalidOperationException("Low thresholds must be less than high thresholds.");
        }

        // None of the numeric values should ever be negative.
        if (LowCpuPercent < 0 || HighCpuPercent < 0 || LowWorkersPercent < 0 || HighWorkersPercent < 0 || LowInstanceCpuPercent < 0 || HighInstanceCpuPercent < 0 || LowDataIoPercent < 0 || HighDataIoPercent < 0 || VCoreFloor < 0 || VCoreCeiling < 0 || RetryCount < 0 || RetryInterval < 0)
        {
            throw new InvalidOperationException("None of the numeric values should be negative.");
        }



        // LongWindowLookback must be greater than ShortWindowLookback
        if (LongWindowLookback <= ShortWindowLookback)
        {
            throw new InvalidOperationException("LongWindowLookback must be greater than ShortWindowLookback.");
        }

        // LongWindowLookback must be positive
        if (LongWindowLookback <= 0)
        {
            throw new InvalidOperationException("LongWindowLookback must be positive.");
        }

        // ShortWindowLookback must be positive
        if (ShortWindowLookback <= 0)
        {
            throw new InvalidOperationException("ShortWindowLookback must be positive.");
        }
    }

    public double GetVCoreFloorForPool(string poolName)
    {
        if (ElasticPools.TryGetValue(poolName, out var customVCoreFloor) && customVCoreFloor.HasValue)
        {
            return customVCoreFloor.Value;
        }
        return VCoreFloor;
    }

    public override string ToString()
    {
        return $"Configuration:\n" +
               $"PoolDbConnection: {PoolDbConnection}\n" +
               $"MetricsSqlConnection: {MetricsSqlConnection}\n" +
               $"MasterSqlConnection: {MasterSqlConnection}\n" +
               $"SubscriptionId: {SubscriptionId}\n" +
               $"SqlInstanceName: {SqlInstanceName}\n" +
               $"ResourceGroupName: {ResourceGroupName}\n" +
               $"ElasticPools: {string.Join(", ", ElasticPools)}\n" +
               $"LowCpuPercent: {LowCpuPercent}\n" +
               $"HighCpuPercent: {HighCpuPercent}\n" +
               $"LowWorkersPercent: {LowWorkersPercent}\n" +
               $"HighWorkersPercent: {HighWorkersPercent}\n" +
               $"LowInstanceCpuPercent: {LowInstanceCpuPercent}\n" +
               $"HighInstanceCpuPercent: {HighInstanceCpuPercent}\n" +
               $"LowDataIoPercent: {LowDataIoPercent}\n" +
               $"HighDataIoPercent: {HighDataIoPercent}\n" +
               $"LongWindowLookback: {LongWindowLookback}\n" +
               $"ShortWindowLookback: {ShortWindowLookback}\n" +
               $"VCoreFloor: {VCoreFloor}\n" +
               $"VCoreCeiling: {VCoreCeiling}\n" +
               $"VCoreOptions: {string.Join(", ", VCoreOptions)}\n" +
               $"PerDatabaseMaximums: {string.Join(", ", PerDatabaseMaximums)}\n" +
               $"IsSentryLoggingEnabled: {IsSentryLoggingEnabled}\n" +
               $"SentryDsn: {SentryDsn}\n" +
               $"RetryCount: {RetryCount}\n" +
               $"RetryInterval: {RetryInterval}\n" +
               $"IsDryRun: {IsDryRun}\n" +
               $"MaxExpectedScalingTimeSeconds: {MaxExpectedScalingTimeSeconds}\n" +
               $"CoolDownPeriodSeconds: {CoolDownPeriodSeconds}\n";
    }

    private static List<double> ParseVCoreList(string vCoreOptions)
    {
        var vCoreOptionsList = vCoreOptions.Split(',')
            .Select(p => p.Trim())
            .ToList();

        var parsedOptions = new List<double>();
        foreach (var option in vCoreOptionsList)
        {
            if (double.TryParse(option, out var parsedOption))
            {
                parsedOptions.Add(parsedOption);
            }
            else
            {
                throw new InvalidOperationException("vCore list must be a comma-separated list of doubles.");
            }
        }

        return parsedOptions;
    }

    public double GetPerDatabaseMaxByVCore(double targetVCore)
    {
        // Look up the array index of the targetVCore
        var index = VCoreOptions.ToList().IndexOf(targetVCore);
        // Fetch the element from the PerDatabaseMaximums array at the same index
        return PerDatabaseMaximums.ElementAt(index);
    }
}