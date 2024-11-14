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
    public List<string> ElasticPools { get; set; }
    public decimal LowCpuPercent { get; }
    public decimal HighCpuPercent { get; }
    public decimal LowWorkersPercent { get; }
    public decimal HighWorkersPercent { get; }
    public decimal LowInstanceCpuPercent { get; }
    public decimal HighInstanceCpuPercent { get; }
    public int LowCountThreshold { get; }
    public int HighCountThreshold { get; }
    public int LookBackSeconds { get; }
    public double VCoreFloor { get; }
    public double VCoreCeiling { get; }
    public List<double> VCoreOptions { get; }

    public List<double> PerDatabaseMaximums { get; }

    public AutoScalerConfiguration(IConfiguration configuration)
    {
        PoolDbConnection = configuration.GetValue<string>("ConnectionStrings:PoolDbConnection") ?? throw new InvalidOperationException("PoolDbConnection is not set.");
        MetricsSqlConnection = configuration.GetValue<string>("ConnectionStrings:MetricsSqlConnection") ?? "";
        MasterSqlConnection = configuration.GetValue<string>("ConnectionStrings:MasterSqlConnection") ?? throw new InvalidOperationException("MasterSqlConnection is not set.");
        SubscriptionId = configuration.GetValue<string>("SubscriptionId") ?? throw new InvalidOperationException("SubscriptionId is not set.");
        SqlInstanceName = configuration.GetValue<string>("SqlInstanceName") ?? throw new InvalidOperationException("SqlInstanceName is not set.");
        ResourceGroupName = configuration.GetValue<string>("ResourceGroupName") ?? throw new InvalidOperationException("ResourceGroupName is not set.");
        ElasticPools = configuration.GetValue<string>("ElasticPools")?.Split(',').Select(p => p.Trim()).ToList() ?? throw new InvalidOperationException("ElasticPools is not set.");

        LowCpuPercent = configuration.GetValue<decimal>("LowCpuPercent");
        HighCpuPercent = configuration.GetValue<decimal>("HighCpuPercent");

        LowWorkersPercent = configuration.GetValue<decimal>("LowWorkersPercent");
        HighWorkersPercent = configuration.GetValue<decimal>("HighWorkersPercent");

        LowInstanceCpuPercent = configuration.GetValue<decimal>("LowInstanceCpuPercent");
        HighInstanceCpuPercent = configuration.GetValue<decimal>("HighInstanceCpuPercent");

        LowCountThreshold = configuration.GetValue<int>("LowCountThreshold");
        HighCountThreshold = configuration.GetValue<int>("HighCountThreshold");
        LookBackSeconds = configuration.GetValue<int>("LookBackSeconds");

        VCoreFloor = configuration.GetValue<double>("VCoreFloor");
        VCoreCeiling = configuration.GetValue<double>("VCoreCeiling");

        VCoreOptions = ParseVCoreList(configuration.GetValue<string>("VCoreOptions") ?? throw new InvalidOperationException("VCoreOptions is not set."));
        PerDatabaseMaximums = ParseVCoreList(configuration.GetValue<string>("PerDatabaseMaximums") ?? throw new InvalidOperationException("PerDatabaseMaximums is not set."));

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
        if (LowCpuPercent >= HighCpuPercent || LowWorkersPercent >= HighWorkersPercent || LowInstanceCpuPercent >= HighInstanceCpuPercent)
        {
            throw new InvalidOperationException("Low thresholds must be less than high thresholds.");
        }

        // None of the numeric values should ever be negative.
        if (LowCpuPercent < 0 || HighCpuPercent < 0 || LowWorkersPercent < 0 || HighWorkersPercent < 0 || LowInstanceCpuPercent < 0 || HighInstanceCpuPercent < 0 || LowCountThreshold < 0 || HighCountThreshold < 0 || LookBackSeconds < 0 || VCoreFloor < 0 || VCoreCeiling < 0)
        {
            throw new InvalidOperationException("None of the numeric values should be negative.");
        }

        Console.WriteLine("Configuration:");
        Console.WriteLine($"PoolDbConnection: {PoolDbConnection}");
        Console.WriteLine($"MetricsSqlConnection: {MetricsSqlConnection}");
        Console.WriteLine($"MasterSqlConnection: {MasterSqlConnection}");
        Console.WriteLine($"SubscriptionId: {SubscriptionId}");
        Console.WriteLine($"SqlInstanceName: {SqlInstanceName}");
        Console.WriteLine($"ResourceGroupName: {ResourceGroupName}");
        Console.WriteLine($"ElasticPools: {string.Join(", ", ElasticPools)}");
        Console.WriteLine($"LowCpuPercent: {LowCpuPercent}");
        Console.WriteLine($"HighCpuPercent: {HighCpuPercent}");
        Console.WriteLine($"LowWorkersPercent: {LowWorkersPercent}");
        Console.WriteLine($"HighWorkersPercent: {HighWorkersPercent}");
        Console.WriteLine($"LowInstanceCpuPercent: {LowInstanceCpuPercent}");
        Console.WriteLine($"HighInstanceCpuPercent: {HighInstanceCpuPercent}");
        Console.WriteLine($"LowCountThreshold: {LowCountThreshold}");
        Console.WriteLine($"HighCountThreshold: {HighCountThreshold}");
        Console.WriteLine($"LookBackSeconds: {LookBackSeconds}");
        Console.WriteLine($"VCoreFloor: {VCoreFloor}");
        Console.WriteLine($"VCoreCeiling: {VCoreCeiling}");
        Console.WriteLine($"VCoreOptions: {string.Join(", ", VCoreOptions)}");
        Console.WriteLine($"PerDatabaseMaximums: {string.Join(", ", PerDatabaseMaximums)}");
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
}