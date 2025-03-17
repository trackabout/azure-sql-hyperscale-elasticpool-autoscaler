using Microsoft.Extensions.Configuration;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class ConfigurationTests
{
    private IConfiguration LoadConfiguration(Dictionary<string, string?>? overrides = null)
    {
        var defaultSettings = new Dictionary<string, string?>
        {
            // Connection strings
            {"ConnectionStrings:PoolDbConnection", "test-pool-sql-connection-string"},
            {"ConnectionStrings:MetricsSqlConnection", "test-metrics-sql-connection-string"},
            {"ConnectionStrings:MasterSqlConnection", "test-master-sql-connection-string"},

            // Settings
            {"SubscriptionId", "test-subscription-id"},
            {"SqlInstanceName", "test-sql-instance-name"},
            {"ResourceGroupName", "test-resource-group-name"},
            {"ElasticPools", "test-pool1,test-pool2,test-pool3"},
            {"LowCpuPercent", "20"},
            {"HighCpuPercent", "70"},
            {"LowWorkersPercent", "30"},
            {"HighWorkersPercent", "50"},
            {"LowInstanceCpuPercent", "20"},
            {"HighInstanceCpuPercent", "70"},
            {"LowDataIoPercent", "20"},
            {"HighDataIoPercent", "70"},
            {"VCoreFloor", "4"},
            {"VCoreCeiling", "16"},
            {"VCoreOptions", "4,6,8,10,12,14,16,18,20,24,32,40,64,80,128"},
            {"PerDatabaseMaximums", "2,4,6,6,8,10,12,14,14,18,24,32,40,40,80"},
            {"LongWindowSeconds", "1800"},
            {"ShortWindowSeconds", "300"},
            {"RetryCount", "3"},
            {"RetryInterval", "5"},
            {"IsDryRun", "false"},
            {"MaxExpectedScalingTimeSeconds", "300"},
            {"CoolDownPeriodSeconds", "300"},
        };

        if (overrides != null)
        {
            foreach (var item in overrides)
            {
                defaultSettings[item.Key] = item.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaultSettings)
            .Build();
    }

    [Fact]
    public void ValidConfiguration_DoesNotThrow()
    {
        var configuration = LoadConfiguration();
        var autoScalerConfig = new AutoScalerConfiguration(configuration);

        Assert.NotNull(autoScalerConfig);
    }

    [Fact]
    public void MissingPoolDbConnection_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "ConnectionStrings:PoolDbConnection", null } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("PoolDbConnection is not set.", exception.Message);
    }

    [Fact]
    public void VCoreOptionsAndPerDatabaseMaximumsCountMismatch_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "PerDatabaseMaximums", "2,4,6" } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("VCoreOptions and PerDatabaseMaximums must have the same number of elements.", exception.Message);
    }

    [Fact]
    public void VCoreFloorOutsideVCoreOptionsRange_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "VCoreFloor", "100" } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("VCoreFloor must be found within VCoreOptions.", exception.Message);
    }

    [Fact]
    public void VCoreCeilingOutsideVCoreOptionsRange_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "VCoreCeiling", "2" } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("VCoreCeiling must be found within VCoreOptions.", exception.Message);
    }

    [Fact]
    public void VCoreFloorGreaterThanOrEqualToCeiling_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?>
        {
            { "VCoreFloor", "20" },
            { "VCoreCeiling", "10" }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("VCoreFloor must be less than VCoreCeiling.", exception.Message);
    }

    [Fact]
    public void NegativeNumericValues_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "LowCpuPercent", "-1" } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("None of the numeric values should be negative.", exception.Message);
    }

    [Fact]
    public void InvalidThresholdRelationships_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?>
        {
            { "LowCpuPercent", "80" },
            { "HighCpuPercent", "70" }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("Low thresholds must be less than high thresholds.", exception.Message);
    }

    [Fact]
    public void InvalidVCoreListFormat_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "VCoreOptions", "4,8,foo,16" } });
        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("vCore list must be a comma-separated list of doubles.", exception.Message);
    }

    [Fact]
    public void MissingElasticPools_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "ElasticPools", null } });
        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("ElasticPools is not set.", exception.Message);
    }

    [Fact]
    public void MissingSubscriptionId_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "SubscriptionId", null } });
        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("SubscriptionId is not set.", exception.Message);
    }

    [Fact]
    public void MissingMasterSqlConnection_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "ConnectionStrings:MasterSqlConnection", null } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("MasterSqlConnection is not set.", exception.Message);
    }

    [Fact]
    public void LongWindowLookback_LessThan_ShortWindowLookback_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?>
        {
            { "LongWindowLookback", "200" },
            { "ShortWindowLookback", "300" }
        });
        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
    }

    [Fact]
    public void LongWindowLookbackNegative_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?>
        {
            { "LongWindowLookback", "-1" },
            { "ShortWindowLookback", "-10" },
        });
        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("LongWindowLookback must be positive.", exception.Message);
    }


    [Fact]
    public void ShortWindowLookbackNegative_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?>
        {
            { "LongWindowLookback", "1800" },
            { "ShortWindowLookback", "-1" },
        });
        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("ShortWindowLookback must be positive.", exception.Message);
    }


    [Fact]
    public void AutoScalerConfiguration_ParsesElasticPoolsWithCustomVCoreFloors()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "ElasticPools", "ProdDbPoolHS1,ProdDbPoolHS2:8" } });
        var autoScalerConfig = new AutoScalerConfiguration(configuration);

        Assert.Equal(2, autoScalerConfig.ElasticPools.Count);
        Assert.True(autoScalerConfig.ElasticPools.ContainsKey("ProdDbPoolHS1"));
        Assert.True(autoScalerConfig.ElasticPools.ContainsKey("ProdDbPoolHS2"));
        Assert.Null(autoScalerConfig.ElasticPools["ProdDbPoolHS1"]);
        Assert.Equal(8, autoScalerConfig.ElasticPools["ProdDbPoolHS2"]);
    }

    [Fact]
    public void GetVCoreFloorForPool_ReturnsCorrectVCoreFloor()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { {
            "ElasticPools", "ProdDbPoolHS1,ProdDbPoolHS2:8,ProdDbPoolHS3,ProdDbPoolHS4:10" }, {
            "VCoreFloor", "6"
            } });
        var autoScalerConfig = new AutoScalerConfiguration(configuration);

        Assert.Equal(6, autoScalerConfig.GetVCoreFloorForPool("ProdDbPoolHS1"));
        Assert.Equal(8, autoScalerConfig.GetVCoreFloorForPool("ProdDbPoolHS2"));
        Assert.Equal(6, autoScalerConfig.GetVCoreFloorForPool("ProdDbPoolHS3"));
        Assert.Equal(10, autoScalerConfig.GetVCoreFloorForPool("ProdDbPoolHS4"));
    }
    [Fact]
    public void CustomPoolFloorOutOfBounds_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?>
    {
        { "ElasticPools", "ProdDbPoolHS1:200" } // 200 is out of bounds of VCoreOptions
    });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
    }

    [Fact]
    public void ValidConfiguration_Succeeds()
    {
        // Arrange
        var config = LoadConfiguration(new Dictionary<string, string?>
        {
            {"ConnectionStrings:PoolDbConnection", "test-pool-conn"},
            {"ConnectionStrings:MasterSqlConnection", "test-master-conn"},
            {"SubscriptionId", "abc123"},
            {"ElasticPools", "MyPool"},
            {"VCoreFloor", "4"},
            {"VCoreCeiling", "16"},
            {"VCoreOptions", "4,6,8,16"},
            {"PerDatabaseMaximums", "2,4,8,12"},
            {"LowCpuPercent","10"},
            {"HighCpuPercent","80"},
        });

        // Act
        var autoScalerConfig = new AutoScalerConfiguration(config);

        // Assert
        Assert.NotNull(autoScalerConfig);
        Assert.Equal(4, autoScalerConfig.VCoreFloor);
        Assert.Equal(16, autoScalerConfig.VCoreCeiling);
    }

    [Fact]
    public void MissingRequiredValue_ThrowsException()
    {
        // Arrange
        var config = LoadConfiguration(new Dictionary<string, string?> {
            {"ConnectionStrings:PoolDbConnection", null}
        });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(config));
    }
}
