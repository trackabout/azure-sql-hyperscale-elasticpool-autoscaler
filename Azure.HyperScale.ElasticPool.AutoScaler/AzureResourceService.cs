using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Tasks;

namespace Azure.HyperScale.ElasticPool.AutoScaler;

public class AzureResourceService(AutoScalerConfiguration config, ILogger<AzureResourceService> logger, IErrorRecorder errorRecorder, ISqlRepository sqlRepository) : IAzureResourceService
{
    private readonly AutoScalerConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<AzureResourceService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IErrorRecorder _errorRecorder = errorRecorder ?? throw new ArgumentNullException(nameof(errorRecorder));
    private readonly ISqlRepository _sqlRepository = sqlRepository ?? throw new ArgumentNullException(nameof(sqlRepository));

    /// <summary>
    /// Fetches the Elastic Pool Azure resource.
    /// </summary>
    /// <param name="resourceGroupName">The resource group name.</param>
    /// <param name="serverName">The SQL Server name.</param>
    /// <param name="elasticPoolName">The Elastic Pool name.</param>
    /// <returns>The Elastic Pool Azure resource.</returns>
    public async Task<ElasticPoolResource?> GetElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName)
    {
        try
        {
            // Get the subscription using the _subscriptionId
            var armClient = new ArmClient(new DefaultAzureCredential());
            var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{_config.SubscriptionId}"));
            var resourceGroup = await subscription.GetResourceGroups().GetAsync(resourceGroupName).ConfigureAwait(false);
            var sqlServer = await resourceGroup.Value.GetSqlServers().GetAsync(serverName).ConfigureAwait(false);
            return await sqlServer.Value.GetElasticPools().GetAsync(elasticPoolName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex, $"Failed to get Azure Resource Elastic Pool '{elasticPoolName}' in server '{serverName}' and resource group '{resourceGroupName}'.");
            return null;
        }
    }

    /// <summary>
    /// Check permissions to elastic pools
    /// </summary>
    /// <returns>True if permissions are granted, false otherwise.</returns>
    public async Task<bool> CheckPermissionsAsync()
    {
        try
        {
            var elasticPools = _config.ElasticPools.Keys.ToList();
            foreach (var pool in elasticPools)
            {
                var elasticPool = await GetElasticPoolAsync(_config.ResourceGroupName, _config.SqlInstanceName, pool).ConfigureAwait(false);
                if (elasticPool == null)
                {
                    _logger.LogError($"Failed to access elastic pool: {pool}");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex, "Error while checking permissions to access SQL server or elastic pools.");
            return false;
        }
    }

    /// <summary>
    /// Scales the specified HyperScale Elastic Pool to the desired vCore count.
    /// </summary>
    /// <param name="resourceGroupName">The resource group name.</param>
    /// <param name="serverName">The SQL Server name.</param>
    /// <param name="elasticPoolName">The Elastic Pool name.</param>
    /// <param name="newPoolSettings">The new pool settings.</param>
    /// <param name="usageInfo">The usage information.</param>
    /// <param name="currentVCore">The current vCore count.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ScaleElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName, PoolTargetSettings newPoolSettings, UsageInfo usageInfo, double currentVCore)
    {
        try
        {
            var elasticPool = await GetElasticPoolAsync(resourceGroupName, serverName, elasticPoolName).ConfigureAwait(false);

            if (elasticPool == null)
            {
                _errorRecorder.RecordError($"Elastic Pool Azure resource '{elasticPoolName}' not found.");
                return;
            }

            // Check if the Elastic Pool is already at the desired vCore count
            if (elasticPool.Data.Sku.Capacity == (int)newPoolSettings.VCore)
            {
                _errorRecorder.RecordError($"{elasticPoolName}: Pool is already at {newPoolSettings.VCore} vCores. Nothing to do.");
                return;
            }

            // Update the SKU to the desired vCore count
            var patch = new ElasticPoolPatch
            {
                Sku = new SqlSku(elasticPool.Data.Sku.Name)
                {
                    Name = elasticPool.Data.Sku.Name,
                    Tier = "Hyperscale",
                    Capacity = (int)newPoolSettings.VCore
                },
                PerDatabaseSettings = new ElasticPoolPerDatabaseSettings
                {
                    MinCapacity = newPoolSettings.PerDbMinCapacity,
                    MaxCapacity = newPoolSettings.PerDbMaxCapacity
                }
            };

            // Apply the updated configuration.
            // Fire and forget the scaling operation. We're not going to wait around for it to finish.
            // It could take >2 minutes, and we have other pools to scale.
            // We'll be checking on re-execution of this function whether any pools are in transition before we attempt
            // any other scaling operations on them.
            await elasticPool.UpdateAsync(WaitUntil.Started, patch).ConfigureAwait(false);

            // Write new target SLO to monitor table
            await _sqlRepository.WriteToAutoScaleMonitorTableAsync(usageInfo, currentVCore, newPoolSettings.VCore).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Check if this is the geo-replication error (40940)
            try
            {
                if (ex.Message.Contains("ElasticPoolUpdateLinksNotInCatchup") ||
                    ex.Message.Contains("40940"))
                {
                    _logger.LogWarning($"{elasticPoolName}: Scaling operation failed due to geo-replication activity. Will retry on next execution cycle. Error: {ex.Message}");

                    // Log the error and the intent to retry in the monitoring table
                    await _sqlRepository.WriteToAutoScaleMonitorTableAsync(
                        usageInfo,
                        currentVCore,
                        newPoolSettings.VCore,
                        isGeoReplicationDelay: true).ConfigureAwait(false);
                }
                else
                {
                    _errorRecorder.RecordError(ex, $"{elasticPoolName}: Failed to scale pool to {newPoolSettings.VCore} vCores due to a conflict error.");
                }
            }
            catch
            {
                // If there's an error parsing the response, just log the original exception
                _errorRecorder.RecordError(ex, $"{elasticPoolName}: Failed to scale pool to {newPoolSettings.VCore} vCores due to a conflict error.");
            }
        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex, $"{elasticPoolName}: Failed to scale pool to {newPoolSettings.VCore} vCores.");
        }
    }
}