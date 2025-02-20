using Microsoft.Extensions.Configuration;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class ConfigurationTests
{
    private IConfiguration LoadConfiguration(Dictionary<string, string?>? overrides = null)
    {
        var defaultSettings = new Dictionary<string, string?>
        {
            {"ConnectionStrings:PoolDbConnection", "test-pool-sql-connection-string"},
            {"ConnectionStrings:MetricsSqlConnection", "test-metrics-sql-connection-string"},
            {"ConnectionStrings:MasterSqlConnection", "test-master-sql-connection-string"},
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
            {"LowCountThreshold", "5"},
            {"HighCountThreshold", "15"},
            {"LowDataIoPercent", "20"},
            {"HighDataIoPercent", "70"},
            {"LookBackSeconds", "900"},
            {"VCoreFloor", "4"},
            {"VCoreCeiling", "16"},
            {"VCoreOptions", "4,6,8,10,12,14,16,18,20,24,32,40,64,80,128"},
            {"PerDatabaseMaximums", "2,4,6,6,8,10,12,14,14,18,24,32,40,40,80"}
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
    public void InvalidLookBackSeconds_ThrowsException()
    {
        var configuration = LoadConfiguration(new Dictionary<string, string?> { { "LookBackSeconds", "-1" } });

        var exception = Assert.Throws<InvalidOperationException>(() => new AutoScalerConfiguration(configuration));
        Assert.Contains("None of the numeric values should be negative.", exception.Message);
    }
}
