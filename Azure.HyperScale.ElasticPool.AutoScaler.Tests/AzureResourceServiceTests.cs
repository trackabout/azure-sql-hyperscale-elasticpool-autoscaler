using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

/// <summary>
/// ScaleElasticPoolAsync reports whether the scaling attempt failed, so the caller
/// can withhold the run heartbeat. Outcomes the autoscaler expects and handles
/// (already at the target size, a geo-replication conflict it retries later) are not failures.
/// </summary>
public class AzureResourceServiceTests
{
    private const string PoolName = "example-pool-1";
    private const int CurrentVCore = 6;
    private const int TargetVCore = 8;

    private readonly Mock<ElasticPoolResource> _poolMock = new();
    private readonly Mock<IErrorRecorder> _errorRecorderMock = new();

    // Replaces the Azure lookup, which needs real credentials, with a canned pool.
    private sealed class ServiceWithCannedPool(
        AutoScalerConfiguration config,
        IErrorRecorder errorRecorder,
        ElasticPoolResource? pool)
        : AzureResourceService(config, Mock.Of<ILogger<AzureResourceService>>(), errorRecorder, Mock.Of<ISqlRepository>())
    {
        public override Task<ElasticPoolResource?> GetElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName)
        {
            return Task.FromResult(pool);
        }
    }

    private static AutoScalerConfiguration BuildConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            {"ConnectionStrings:PoolDbConnection", "example-pool-db-connection-string"},
            {"ConnectionStrings:MetricsSqlConnection", "example-metrics-connection-string"},
            {"ConnectionStrings:MasterSqlConnection", "example-master-connection-string"},
            {"SubscriptionId", "example-subscription-id"},
            {"SqlInstanceName", "example-sql-server"},
            {"ResourceGroupName", "example-rg"},
            {"ElasticPools", PoolName},
            {"LowCpuPercent", "10"},
            {"HighCpuPercent", "80"},
            {"LowWorkersPercent", "20"},
            {"HighWorkersPercent", "90"},
            {"LowInstanceCpuPercent", "15"},
            {"HighInstanceCpuPercent", "85"},
            {"LowDataIoPercent", "10"},
            {"HighDataIoPercent", "80"},
            {"VCoreFloor", "6"},
            {"VCoreCeiling", "24"},
            {"VCoreOptions", "4,6,8,10,12,14,16,18,20,24"},
            {"PerDatabaseMaximums", "2,4,6,6,8,10,12,14,14,18"},
        };

        return new AutoScalerConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private void GivenPoolAt(int vCore)
    {
        var data = new ElasticPoolData(AzureLocation.EastUS) { Sku = new SqlSku("HS_Gen5") { Capacity = vCore } };
        _poolMock.Setup(p => p.Data).Returns(data);
    }

    private void GivenUpdateThrows(Exception exception)
    {
        _poolMock.Setup(p => p.UpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<ElasticPoolPatch>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    private Task<bool> Scale(ElasticPoolResource? pool)
    {
        var service = new ServiceWithCannedPool(BuildConfiguration(), _errorRecorderMock.Object, pool);
        return service.ScaleElasticPoolAsync(
            "example-rg", "example-sql-server", PoolName,
            new PoolTargetSettings(TargetVCore, 6),
            new UsageInfo { ElasticPoolName = PoolName, ElasticPoolCpuLimit = CurrentVCore },
            CurrentVCore);
    }

    [Fact]
    public async Task ScaleElasticPoolAsync_UpdateAccepted_ReturnsTrue()
    {
        GivenPoolAt(CurrentVCore);
        _poolMock.Setup(p => p.UpdateAsync(It.IsAny<WaitUntil>(), It.IsAny<ElasticPoolPatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ArmOperation<ElasticPoolResource>>());

        Assert.True(await Scale(_poolMock.Object));
    }

    [Fact]
    public async Task ScaleElasticPoolAsync_PoolAlreadyAtTarget_ReturnsTrue()
    {
        GivenPoolAt(TargetVCore);

        Assert.True(await Scale(_poolMock.Object));
    }

    [Fact]
    public async Task ScaleElasticPoolAsync_GeoReplicationConflict_ReturnsTrue()
    {
        // Azure refuses the update while a geo-replica catches up. The next run retries.
        GivenPoolAt(CurrentVCore);
        GivenUpdateThrows(new RequestFailedException(409, "ElasticPoolUpdateLinksNotInCatchup (40940)"));

        Assert.True(await Scale(_poolMock.Object));
    }

    [Fact]
    public async Task ScaleElasticPoolAsync_OtherConflict_ReturnsFalse()
    {
        GivenPoolAt(CurrentVCore);
        GivenUpdateThrows(new RequestFailedException(409, "Another operation is in progress."));

        Assert.False(await Scale(_poolMock.Object));
    }

    [Fact]
    public async Task ScaleElasticPoolAsync_UpdateForbidden_ReturnsFalse()
    {
        // An identity that can read the pool but not update it passes the permission check.
        GivenPoolAt(CurrentVCore);
        GivenUpdateThrows(new RequestFailedException(403, "The client does not have authorization to perform this action."));

        Assert.False(await Scale(_poolMock.Object));
    }

    [Fact]
    public async Task ScaleElasticPoolAsync_PoolLookupFails_ReturnsFalse()
    {
        Assert.False(await Scale(null));
    }
}
