using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class AutoScalerTests
{
    private readonly AutoScaler _autoScaler;
    private readonly AutoScalerConfiguration _config;

    public AutoScalerTests()
    {
        var inMemorySettings = new Dictionary<string, string>
        {
            {"ConnectionStrings:PoolDbConnection", "test-pool-db-sql-connection-string"},
            {"ConnectionStrings:MetricsSqlConnection", "test-metrics-sql-connection-string"},
            {"ConnectionStrings:MasterSqlConnection", "test-master-sql-connection-string"},
            {"SubscriptionId", "test-subscription-id"},
            {"SqlInstanceName", "test-sql-instance-name"},
            {"ResourceGroupName", "test-resource-group-name"},
            {"ElasticPools", "test-pool1,test-pool2,test-pool3"},

            {"LowCpuPercent", "10"},
            {"LowWorkersPercent", "20"},
            {"LowInstanceCpuPercent", "15"},

            {"HighCpuPercent", "80"},
            {"HighWorkersPercent", "90"},
            {"HighInstanceCpuPercent", "85"},

            {"HighCountThreshold", "5"},
            {"LowCountThreshold", "5"},
            {"LookBackSeconds", "900"},
    
            {"VCoreFloor", "6"},
            {"VCoreCeiling", "24"},
            {"VCoreOptions", "4,6,8,10,12,14,16,18,20,24,32,40,64,80,128"},
            {"PerDatabaseMaximums",  "2,4,6,6,8,10,12,14,14,18,24,32,40,40,80"}
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();

        _config = new AutoScalerConfiguration(configuration);
        Mock<ILogger<AutoScaler>> loggerMock = new();
        _autoScaler = new AutoScaler(loggerMock.Object, _config);
    }
    
    [Fact]
    public void ScaleUp_AtHighCpuCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighCpuCount = 5
        };
        const int currentCpu = 4;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtHighWorkerCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighWorkerCount = 5
        };
        const int currentCpu = 4;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtHighInstanceCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighInstanceCpuCount = 5
        };
        const int currentCpu = 12;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighCpuCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighCpuCount = 20 
        };
        const int currentCpu = 4;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighWorkerCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighWorkerCount = 25
        };
        const int currentCpu = 4;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighInstanceCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighInstanceCpuCount = 5
        };
        const int currentCpu = 12;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AllCountsExceededThreshold()
    {
        var usageInfo = new UsageInfo
        {
            HighCpuCount = 20,
            HighWorkerCount = 20,
            HighInstanceCpuCount = 20
        };
        const int currentCpu = 12;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void Hold_OnlyOneLowThresholdMet()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 0,
            LowWorkerCount = 5,
            LowInstanceCpuCount = 35
        };
        const int currentCpu = 10;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(10, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(6, result.PerDbMaxCapacity);
    }

    [Fact]
    public void Hold_OnlyTwoLowThresholdsMet()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 0,
            LowWorkerCount = 5,
            LowInstanceCpuCount = 5
        };
        const int currentCpu = 10;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(10, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(6, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_LowAllThresholdsMet()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 5,  
            LowWorkerCount = 5,
            LowInstanceCpuCount = 5
        };
        const int currentCpu = 10;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(8, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(6, result.PerDbMaxCapacity);
    }

    [Fact]
    public void NoChange_CurrentWithinBounds()
    {
        var usageInfo = new UsageInfo
        {
            HighCpuCount = 0,
            HighWorkerCount = 0,
            HighInstanceCpuCount = 0,
            LowCpuCount = 0,
            LowWorkerCount = 0,
            LowInstanceCpuCount = 0
        };
        const int currentCpu = 12;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(12, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(8, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_JustBelowCeiling_ScalesToCeiling()
    {
        var usageInfo = new UsageInfo
        {
            HighCpuCount = 5,
        };
        const int currentCpu = 20;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreCeiling, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(18, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtCeiling_StaysAtCeiling()
    {
        var usageInfo = new UsageInfo
        {
            HighInstanceCpuCount = 5
        };
        var currentCpu = _config.VCoreCeiling;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreCeiling, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(18, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_JustAboveFloor_ScalesToFloor()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 10,
            LowWorkerCount = 10,
            LowInstanceCpuCount = 10
        };
        const int currentCpu = 8;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreFloor, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_AtFloor_StaysAtFloor()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 5,
            LowWorkerCount = 5,
            LowInstanceCpuCount = 5
        };
        var currentCpu = _config.VCoreFloor;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreFloor, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_SetToFloorWhenBelowFloor()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 5,
            LowWorkerCount = 5,
            LowInstanceCpuCount = 5
        };
        const int currentCpu = 4; // Below configured floor

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreFloor, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_SetToCeilingWhenAboveCeiling()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 5,
            LowWorkerCount = 5,
            LowInstanceCpuCount = 5
        };
        const int currentCpu = 128; // Well above configured ceiling

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreCeiling, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(18, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_ComeDownFromCeiling()
    {
        var usageInfo = new UsageInfo
        {
            LowCpuCount = 5,
            LowWorkerCount = 5,
            LowInstanceCpuCount = 5
        };
        var currentCpu = _config.VCoreCeiling;

        var result = _autoScaler.CalculatePoolTargetSettings(usageInfo, currentCpu);

        Assert.Equal(20, result.VCore);
        Assert.Equal(0, AutoScaler.PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(14, result.PerDbMaxCapacity);
    }
}