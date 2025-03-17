using Azure.ResourceManager.Sql;

namespace Azure.HyperScale.ElasticPool.AutoScaler;

public interface IAzureResourceService
{
    Task<ElasticPoolResource?> GetElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName);
    Task<bool> CheckPermissionsAsync();
    Task ScaleElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName, PoolTargetSettings newPoolSettings, UsageInfo usageInfo, double currentVCore);
}
