using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class AutoScalerTests
{
    private readonly AutoScaler _autoScaler;
    private readonly AutoScalerConfiguration _config;
    private readonly Mock<ISqlRepository> _sqlRepositoryMock;
    private readonly Mock<IErrorRecorder> _errorRecorderMock;
    private readonly Mock<IAzureResourceService> _azureResourceServiceMock;

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
            {"HighCpuPercent", "80"},

            {"LowWorkersPercent", "20"},
            {"HighWorkersPercent", "90"},

            {"LowInstanceCpuPercent", "15"},
            {"HighInstanceCpuPercent", "85"},

            {"LowDataIoPercent", "10"},
            {"HighDataIoPercent", "80"},

            {"LongWindowLookback", "1800"},
            {"ShortWindowLookback", "300"},

            {"VCoreFloor", "6"},
            {"VCoreCeiling", "24"},

            {"VCoreOptions", "4,6,8,10,12,14,16,18,20,24,32,40,64,80,128"},
            {"PerDatabaseMaximums",  "2,4,6,6,8,10,12,14,14,18,24,32,40,40,80"},

            {"MaxExpectedScalingTimeSeconds", "300"}, // 5m
            {"CoolDownPeriodSeconds", "600"}, // 10m

            {"IsSentryLoggingEnabled", "false"}  // Sensible default
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();

        _config = new AutoScalerConfiguration(configuration);
        Mock<ILogger<AutoScaler>> loggerMock = new();
        _sqlRepositoryMock = new Mock<ISqlRepository>();
        _errorRecorderMock = new Mock<IErrorRecorder>();
        _azureResourceServiceMock = new Mock<IAzureResourceService>();

        _autoScaler = new AutoScaler(loggerMock.Object, _config, _sqlRepositoryMock.Object, _errorRecorderMock.Object, _azureResourceServiceMock.Object);
    }

    [Fact]
    public void ScaleUp_AtHighCpuThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.HighCpuPercent,
            ShortAvgCpu = _config.HighCpuPercent
        };
        const int currentCpu = 4;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtHighWorkerThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongWorkersPercent = _config.HighWorkersPercent,
            ShortWorkersPercent = _config.HighWorkersPercent
        };
        const int currentCpu = 4;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtHighInstanceThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongInstanceCpu = _config.HighInstanceCpuPercent,
            ShortInstanceCpu = _config.HighInstanceCpuPercent
        };
        const int currentCpu = 12;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtHighDataIoThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongDataIo = _config.HighDataIoPercent,
            ShortDataIo = _config.HighDataIoPercent
        };
        const int currentCpu = 12;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighCpuThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.HighCpuPercent + 1,
            ShortAvgCpu = _config.HighCpuPercent + 1
        };
        const int currentCpu = 4;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighWorkerThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongWorkersPercent = _config.HighWorkersPercent + 1,
            ShortWorkersPercent = _config.HighWorkersPercent + 1
        };
        const int currentCpu = 4;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighInstanceThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongInstanceCpu = _config.HighInstanceCpuPercent + 1,
            ShortInstanceCpu = _config.HighInstanceCpuPercent + 1
        };
        const int currentCpu = 12;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AllsExceededThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.HighCpuPercent + 1,
            ShortAvgCpu = _config.HighCpuPercent + 1,
            LongWorkersPercent = _config.HighWorkersPercent + 1,
            ShortWorkersPercent = _config.HighWorkersPercent + 1,
            LongInstanceCpu = _config.HighInstanceCpuPercent + 1,
            ShortInstanceCpu = _config.HighInstanceCpuPercent + 1,
            LongDataIo = _config.HighDataIoPercent + 1,
            ShortDataIo = _config.HighDataIoPercent + 1
        };
        const int currentCpu = 12;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(14, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(10, result.PerDbMaxCapacity);
    }

    [Fact]
    public void Hold_LongAvgCpuOverThreshold_ShortAvgCpuNot()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.HighCpuPercent + 1,
            ShortAvgCpu = _config.HighCpuPercent - 1
        };
        const int currentCpu = 10;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(10, result.VCore);
    }

    [Fact]
    public void Hold_OnlyOneLowThresholdMet()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.LowCpuPercent,
            ShortAvgCpu = _config.HighCpuPercent
        };
        const int currentCpu = 10;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(10, result.VCore);
    }

    [Fact]
    public void ScaleDown_LowAllThresholdsMet()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"
        };
        const int currentCpu = 10;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(8, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(6, result.PerDbMaxCapacity);
    }

    [Fact]
    public void NoChange_CurrentWithinBounds()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics within high and low thresholds.
            LongAvgCpu = _config.HighCpuPercent - 1,
            ShortAvgCpu = _config.HighCpuPercent - 1,
            LongDataIo = _config.HighDataIoPercent - 1,
            ShortDataIo = _config.HighDataIoPercent - 1,
            LongInstanceCpu = _config.HighInstanceCpuPercent - 1,
            ShortInstanceCpu = _config.HighInstanceCpuPercent - 1,
            LongWorkersPercent = _config.HighWorkersPercent - 1,
            ShortWorkersPercent = _config.HighWorkersPercent - 1

        };
        const int currentCpu = 12;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(12, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(8, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_JustBelowCeiling_ScalesToCeiling()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.HighCpuPercent,
            ShortAvgCpu = _config.HighCpuPercent
        };
        const int currentCpu = 20;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreCeiling, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(18, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_AtCeiling_StaysAtCeiling()
    {
        var usageInfo = new UsageInfo
        {
            LongAvgCpu = _config.HighCpuPercent,
            ShortAvgCpu = _config.HighCpuPercent
        };
        var currentCpu = _config.VCoreCeiling;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreCeiling, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(18, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_JustAboveFloor_ScalesToFloor()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"
        };
        const int currentCpu = 8;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreFloor, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_AtFloor_StaysAtFloor()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"
        };
        var currentCpu = _config.VCoreFloor;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreFloor, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_SetToFloorWhenBelowFloor()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"
        };
        const int currentCpu = 4; // Below configured floor

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreFloor, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_SetToCeilingWhenAboveCeiling()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"
        };
        const int currentCpu = 128; // Well above configured ceiling

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(_config.VCoreCeiling, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(18, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleDown_ComeDownFromCeiling()
    {
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"
        };
        var currentCpu = _config.VCoreCeiling;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(20, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(14, result.PerDbMaxCapacity);
    }

    [Fact]
    public void ScaleUp_OverHighDataIoCountThreshold()
    {
        var usageInfo = new UsageInfo
        {
            LongDataIo = _config.HighDataIoPercent + 1,
            ShortDataIo = _config.HighDataIoPercent + 1
        };
        const int currentCpu = 4;

        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentCpu);

        Assert.Equal(6, result.VCore);
        Assert.Equal(0, PoolTargetSettings.PerDbMinCapacity);
        Assert.Equal(4, result.PerDbMaxCapacity);
    }

    // GetNewPoolTarget Tests
    [Fact]
    public void GetNewPoolTarget_ScalesUp_WhenShortAndLongHigh()
    {
        var usageInfo = new UsageInfo
        {
            ShortAvgCpu = _config.HighCpuPercent + 1,
            LongAvgCpu = _config.HighCpuPercent + 1,
        };
        const double currentVCore = 4;
        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentVCore);
        Assert.Equal(6, result.VCore);
    }

    [Fact]
    public void GetNewPoolTarget_ScalesDown_WhenAllShortAndLongLow()
    {
        var usageInfo = new UsageInfo
        {
            ShortAvgCpu = _config.LowCpuPercent - 1,
            LongAvgCpu = _config.LowCpuPercent - 1,
            ShortDataIo = _config.LowDataIoPercent - 1,
            LongDataIo = _config.LowDataIoPercent - 1,
            ShortInstanceCpu = _config.LowInstanceCpuPercent - 1,
            LongInstanceCpu = _config.LowInstanceCpuPercent - 1,
            ShortWorkersPercent = _config.LowWorkersPercent - 1,
            LongWorkersPercent = _config.LowWorkersPercent - 1
        };
        const double currentVCore = 8;
        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentVCore);
        Assert.Equal(6, result.VCore);
    }

    [Fact]
    public void GetNewPoolTarget_Holds_WhenInBetweenThresholds()
    {
        var usageInfo = new UsageInfo
        {
            // Set all the values just above their low threshold.
            ShortAvgCpu = _config.LowCpuPercent + 1,
            LongAvgCpu = _config.LowCpuPercent + 1,
            ShortDataIo = _config.LowDataIoPercent + 1,
            LongDataIo = _config.LowDataIoPercent + 1,
            ShortInstanceCpu = _config.LowInstanceCpuPercent + 1,
            LongInstanceCpu = _config.LowInstanceCpuPercent + 1,
            ShortWorkersPercent = _config.LowWorkersPercent + 1,
            LongWorkersPercent = _config.LowWorkersPercent + 1
        };
        const double currentVCore = 6;
        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentVCore);
        Assert.Equal(currentVCore, result.VCore);
    }

    [Fact]
    public void GetNewPoolTarget_RisesToFloor_WhenBelowFloor()
    {
        // Simulate usage that indicates scaling up, with a currentVCore below the floor
        var usageInfo = new UsageInfo
        {
            ShortAvgCpu = _config.HighCpuPercent + 1,
            LongAvgCpu = _config.HighCpuPercent + 1,
        };
        const double currentVCore = 4;
        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentVCore);
        Assert.Equal(_config.VCoreFloor, result.VCore);
    }

    [Fact]
    public void GetNewPoolTarget_LowerToCeiling_WhenAboveCeiling()
    {
        // Simulate usage that indicates scaling down, with currentVCore above the ceiling
        var usageInfo = new UsageInfo
        {
            // All metrics are 0 by default, so "low"u
        };
        const double currentVCore = 128;
        var result = _autoScaler.GetNewPoolTarget(usageInfo, currentVCore);
        Assert.Equal(_config.VCoreCeiling, result.VCore);
    }

    [Fact]
    public async Task DoTheThing_DryRunEnabled_ShouldNotScale()
    {
        _config.IsDryRun = true;  // Enable dry run

        // Arrange
        _sqlRepositoryMock.Setup(repo => repo.GetPoolsToConsider())
            .ReturnsAsync(["test-pool1"]);

        _sqlRepositoryMock.Setup(repo => repo.SamplePoolMetricsAsync(It.IsAny<List<string>>()))
            .ReturnsAsync(new List<UsageInfo>
            {
                new() {
                    ElasticPoolName = "test-pool1",
                    ElasticPoolCpuLimit = 4,
                    ShortAvgCpu = _config.HighCpuPercent + 1,
                    LongAvgCpu = _config.HighCpuPercent + 1
                }
            });

        _azureResourceServiceMock.Setup(service => service.CheckPermissionsAsync())
            .ReturnsAsync(true);

        // Act
        var result = await _autoScaler.DoTheThing();

        // Assert
        Assert.True(result);
        _azureResourceServiceMock.Verify(service => service.ScaleElasticPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PoolTargetSettings>(), It.IsAny<UsageInfo>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task DoTheThing_DryRunDisabled_ShouldScale()
    {
        // Arrange
        _config.IsDryRun = false;  // Disable dry run

        _sqlRepositoryMock.Setup(repo => repo.GetPoolsToConsider())
            .ReturnsAsync(["test-pool1"]);

        _sqlRepositoryMock.Setup(repo => repo.SamplePoolMetricsAsync(It.IsAny<List<string>>()))
            .ReturnsAsync(
            [
                new UsageInfo
                {
                    ElasticPoolName = "test-pool1",
                    ElasticPoolCpuLimit = 4,
                    ShortAvgCpu = _config.HighCpuPercent + 1,
                    LongAvgCpu = _config.HighCpuPercent + 1
                }
            ]);

        _azureResourceServiceMock.Setup(service => service.CheckPermissionsAsync())
            .ReturnsAsync(true);

        // Act
        var result = await _autoScaler.DoTheThing();

        // Assert
        Assert.True(result);
        _azureResourceServiceMock.Verify(service => service.ScaleElasticPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PoolTargetSettings>(), It.IsAny<UsageInfo>(), It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task DoTheThing_NoThresholdsExceeded_ShouldHold()
    {
        // Arrange
        _config.IsDryRun = false;  // Disable dry run

        _sqlRepositoryMock.Setup(repo => repo.GetPoolsToConsider())
            .ReturnsAsync(["test-pool1"]);

        _sqlRepositoryMock.Setup(repo => repo.SamplePoolMetricsAsync(It.IsAny<List<string>>()))
            .ReturnsAsync(
            [
                new UsageInfo
                {
                    ElasticPoolName = "test-pool1",
                    ElasticPoolCpuLimit = 4,
                    ShortAvgCpu = _config.LowCpuPercent + 1,
                    LongAvgCpu = _config.LowCpuPercent + 1
                }
            ]);

        _azureResourceServiceMock.Setup(service => service.CheckPermissionsAsync())
            .ReturnsAsync(true);

        // Act
        var result = await _autoScaler.DoTheThing();

        // Assert
        Assert.True(result);
        _azureResourceServiceMock.Verify(service => service.ScaleElasticPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PoolTargetSettings>(), It.IsAny<UsageInfo>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task DoTheThing_AllPoolsDisqualified_ExitEarly()
    {
        // Arrange
        _config.IsDryRun = false;  // Disable dry run

        _sqlRepositoryMock.Setup(repo => repo.GetPoolsToConsider())
            .ReturnsAsync([]);

        _azureResourceServiceMock.Setup(service => service.CheckPermissionsAsync())
            .ReturnsAsync(true);

        // Act
        var result = await _autoScaler.DoTheThing();

        // Assert
        Assert.False(result);
        _azureResourceServiceMock.Verify(service => service.ScaleElasticPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PoolTargetSettings>(), It.IsAny<UsageInfo>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task DoTheThing_PermissionFailure_ExitEarly()
    {
        // Arrange
        _config.IsDryRun = false;  // Disable dry run

        _sqlRepositoryMock.Setup(repo => repo.GetPoolsToConsider())
            .ReturnsAsync(["test-pool1"]);

        _azureResourceServiceMock.Setup(service => service.CheckPermissionsAsync())
            .ReturnsAsync(false);

        // Act
        var result = await _autoScaler.DoTheThing();

        // Assert
        Assert.False(result);
        _azureResourceServiceMock.Verify(service => service.ScaleElasticPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PoolTargetSettings>(), It.IsAny<UsageInfo>(), It.IsAny<double>()), Times.Never);
    }
}