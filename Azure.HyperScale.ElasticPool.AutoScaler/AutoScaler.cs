using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Sql.Models;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Azure.HyperScale.ElasticPool.AutoScaler;

public enum ScalingActions
{
    Up,
    Down,
    Hold
}

public class AutoScaler(
    ILogger<AutoScaler> logger,
    AutoScalerConfiguration autoScalerConfig,
    ISqlRepository sqlRepository,
    IErrorRecorder errorRecorder)
{
    private const string HyperScaleTier = "Hyperscale";

    private IErrorRecorder _errorRecorder = errorRecorder ?? throw new ArgumentNullException(nameof(errorRecorder));
    private ISqlRepository _sqlRepository = sqlRepository ?? throw new ArgumentNullException(nameof(sqlRepository));
    private ILogger<AutoScaler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private AutoScalerConfiguration _config = autoScalerConfig ?? throw new ArgumentNullException(nameof(autoScalerConfig));

    [Function("AutoScaler")]
    public async Task Run([TimerTrigger("*/15 * * * * *")] TimerInfo myTimer)
    {
        try
        {
            _logger.LogInformation("==================================================================================");
            _logger.LogInformation("--------------------------------- AutoScaler Run ---------------------------------");
            // Check permissions
            var hasPermissions = await CheckPermissionsAsync().ConfigureAwait(false);
            if (!hasPermissions)
            {
                _logger.LogError("Insufficient permissions to access SQL server or elastic pools.");
                return;
            }

            // Check for pending scaling operations
            var poolsInTransition = await _sqlRepository.GetPoolsInTransitionAsync().ConfigureAwait(false);
            var poolsToConsider = _config.ElasticPools.Keys.ToList();

            // If there are ongoing operations for specific elastic pools, exclude them from the rest of this execution.
            if (poolsInTransition != null)
            {
                // Case-insensitive comparison of pool names, just in case we mistype them in the config.
                // Remove from consideration any pools that are in transition.
                var poolsInTransitionSet = new HashSet<string>(poolsInTransition, StringComparer.OrdinalIgnoreCase);
                poolsToConsider = _config.ElasticPools.Keys
                    .Where(pool => !poolsInTransitionSet.Contains(pool))
                    .ToList();
            }

            // Remove from consideration any pools that completed a scaling operation within the last CoolDownPeriodSeconds.
            var lastScalingOperations = await _sqlRepository.GetLastScalingOperationsAsync().ConfigureAwait(false);
            if (lastScalingOperations != null)
            {
                var poolsToExclude = lastScalingOperations
                    .Where(kv => kv.Value < _config.CoolDownPeriodSeconds)
                    .Select(kv => kv.Key)
                    .ToList();

                // If there are pools to exclude, log them.
                if (poolsToExclude.Count > 0)
                {
                    _logger.LogInformation($"Skipping recently scaled pools due to cooldown period: {string.Join(", ", poolsToExclude)}");
                }

                // Case-insensitive comparison of pool names, just in case we mistype them in the config.
                poolsToConsider = poolsToConsider
                    .Where(pool => !poolsToExclude.Contains(pool, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            // If there are no pools to consider, log and return.
            if (poolsToConsider.Count == 0)
            {
                _logger.LogInformation($"No pools to evaluate this time for server {_config.SqlInstanceName}.");
                return;
            }

            var poolMetrics = await _sqlRepository.SamplePoolMetricsAsync(poolsToConsider).ConfigureAwait(false);
            if (poolMetrics == null)
            {
                _errorRecorder.RecordError($"Unexpected: SamplePoolMetricsAsync() returned null while sampling pool metrics for server {_config.SqlInstanceName}.");
                return;
            }

            await CheckAndScalePoolsAsync(poolMetrics).ConfigureAwait(false);

        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex, "Unexpected error in AutoScaler.Run() function.");
        }
    }

    private async Task CheckAndScalePoolsAsync(IEnumerable<UsageInfo> poolMetrics)
    {
        // Loop through each pool and evaluate the metrics
        foreach (var usageInfo in poolMetrics)
        {
            var serverAndPool = $"{_config.SqlInstanceName}.{usageInfo.ElasticPoolName}";
            _logger.LogInformation($"\n                 --=> Evaluating Pool {serverAndPool} <=--");
            _logger.LogInformation(usageInfo.ToString());

            var currentVCore = (double)usageInfo.ElasticPoolCpuLimit;

            // Figure out the new target vCore.
            var newPoolSettings = CalculatePoolTargetSettings(usageInfo, currentVCore);

            // If the target vCore is the same as the current vCore, no scaling is necessary.
            if (newPoolSettings.VCore.Equals(currentVCore)) continue;

            // We are going to scale!
            try
            {
                if (_config.IsDryRun)
                {
                    _logger.LogWarning($"DRY RUN ENABLED: Would have scaled {serverAndPool} from {currentVCore} to {newPoolSettings.VCore}");
                }
                else
                {
                    _logger.LogWarning($"ACTION!: Scaling {serverAndPool} from {currentVCore} to {newPoolSettings.VCore}");
                    await ScaleElasticPoolAsync(_config.ResourceGroupName,
                        _config.SqlInstanceName, usageInfo.ElasticPoolName, newPoolSettings).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _errorRecorder.RecordError(ex,
                    $"{serverAndPool}: Error while scaling from {currentVCore} to {newPoolSettings.VCore}");
            }

            // Write current SLO to monitor table
            await _sqlRepository.WriteToAutoScaleMonitorTable(usageInfo, currentVCore, newPoolSettings.VCore).ConfigureAwait(false);
        }
    }

    public double GetPerDatabaseMaximumByIndex(double targetVCore)
    {
        // Look up the array index of the targetVCore
        var index = _config.VCoreOptions.ToList().IndexOf(targetVCore);
        // Fetch the element from the PerDatabaseMaximums array at the same index
        return _config.PerDatabaseMaximums.ElementAt(index);
    }

    public PoolTargetSettings CalculatePoolTargetSettings(UsageInfo usageInfo, double currentVCore)
    {
        var targetVCore = currentVCore;
        var perDbMaxCapacity = GetPerDatabaseMaximumByIndex(currentVCore);
        var scalingAction = GetScalingActionFromShortAndLongMetrics(usageInfo);

        switch (scalingAction)
        {
            case ScalingActions.Up:
                targetVCore = GetServiceLevelObjective(currentVCore, ScalingActions.Up, usageInfo.ElasticPoolName);
                perDbMaxCapacity = GetPerDatabaseMaximumByIndex(targetVCore);
                if (targetVCore > currentVCore)
                {
                    _logger.LogWarning($"EVALUATION RESULT: HIGH threshold crossed.");
                }
                break;
            case ScalingActions.Down:
                targetVCore = GetServiceLevelObjective(currentVCore, ScalingActions.Down, usageInfo.ElasticPoolName);
                perDbMaxCapacity = GetPerDatabaseMaximumByIndex(targetVCore);
                if (targetVCore < currentVCore)
                {
                    _logger.LogWarning($"EVALUATION RESULT: LOW threshold crossed.");
                }
                break;

            case ScalingActions.Hold:
                _logger.LogInformation($"EVALUATION RESULT: HOLD, no change required.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scalingAction));
        }

        return new PoolTargetSettings(targetVCore, perDbMaxCapacity);
    }

    private ScalingActions GetScalingActionFromShortAndLongMetrics(UsageInfo usageInfo)
    {
        // Short-window checks
        bool shortCpuHigh = usageInfo.ShortAvgCpu >= _config.HighCpuPercent;
        bool shortWorkersHigh = usageInfo.ShortWorkersPercent >= _config.HighWorkersPercent;
        bool shortInstCpuHigh = usageInfo.ShortInstanceCpu >= _config.HighInstanceCpuPercent;
        bool shortDataIoHigh = usageInfo.ShortDataIo >= _config.HighDataIoPercent;

        bool shortCpuLow = usageInfo.ShortAvgCpu <= _config.LowCpuPercent;
        bool shortWorkersLow = usageInfo.ShortWorkersPercent <= _config.LowWorkersPercent;
        bool shortInstCpuLow = usageInfo.ShortInstanceCpu <= _config.LowInstanceCpuPercent;
        bool shortDataIoLow = usageInfo.ShortDataIo <= _config.LowDataIoPercent;

        // Long-window checks
        bool longCpuHigh = usageInfo.LongAvgCpu >= _config.HighCpuPercent;
        bool longWorkersHigh = usageInfo.LongWorkersPercent >= _config.HighWorkersPercent;
        bool longInstCpuHigh = usageInfo.LongInstanceCpu >= _config.HighInstanceCpuPercent;
        bool longDataIoHigh = usageInfo.LongDataIo >= _config.HighDataIoPercent;

        bool longCpuLow = usageInfo.LongAvgCpu <= _config.LowCpuPercent;
        bool longWorkersLow = usageInfo.LongWorkersPercent <= _config.LowWorkersPercent;
        bool longInstCpuLow = usageInfo.LongInstanceCpu <= _config.LowInstanceCpuPercent;
        bool longDataIoLow = usageInfo.LongDataIo <= _config.LowDataIoPercent;

        // Decide scaling
        bool scaleUp = (shortCpuHigh && longCpuHigh) || (shortWorkersHigh && longWorkersHigh) ||
                       (shortInstCpuHigh && longInstCpuHigh) || (shortDataIoHigh && longDataIoHigh);

        bool scaleDown = shortCpuLow && shortWorkersLow && shortInstCpuLow && shortDataIoLow
                         && longCpuLow && longWorkersLow && longInstCpuLow && longDataIoLow;

        if (scaleUp) return ScalingActions.Up;
        if (scaleDown) return ScalingActions.Down;
        return ScalingActions.Hold;
    }

    public double GetServiceLevelObjective(double currentCpu, ScalingActions action, string elasticPoolName)
    {
        var vCoreOptions = _config.VCoreOptions;
        var currentIndex = Array.IndexOf(vCoreOptions.ToArray(), currentCpu);

        // Check for a custom vCore floor setting for this pool
        var vCoreFloor = _config.GetVCoreFloorForPool(elasticPoolName);

        // If currentCpu is not found in the array (this would be unusual), return currentCpu.
        if (currentIndex == -1)
        {
            _errorRecorder.RecordError($"{elasticPoolName}: Current vCore setting of {currentCpu} not found in the list of vCore options.");
            return currentCpu;
        }

        switch (action)
        {
            case ScalingActions.Hold:
                return currentCpu;

            case ScalingActions.Up:
                // If we're below the floor, raise to the floor.
                if (currentCpu < vCoreFloor)
                {
                    _logger.LogWarning($"{elasticPoolName}: Current vCore setting {currentCpu} is below the floor {vCoreFloor}. Raising to floor.");
                    return vCoreFloor;
                }

                // If we're at or above the ceiling, hold at ceiling.
                if (currentCpu >= _config.VCoreCeiling)
                {
                    _logger.LogWarning($"{elasticPoolName}: Current vCore setting {currentCpu} is at or above the ceiling {_config.VCoreCeiling}. Keeping at ceiling.");
                    return _config.VCoreCeiling;
                }

                // Otherwise, look up the correct next step.
                return vCoreOptions.ElementAt(currentIndex + 1);

            case ScalingActions.Down:
                // If we're above the ceiling, bring down to ceiling.
                if (currentCpu > _config.VCoreCeiling)
                {
                    _logger.LogWarning($"{elasticPoolName}: Current vCore setting {currentCpu} is above the ceiling {_config.VCoreCeiling}. Lowering to ceiling.");
                    return _config.VCoreCeiling;
                }

                // If we're at or below the floor, hold at the floor.
                if (currentCpu <= vCoreFloor)
                {
                    _logger.LogInformation($"{elasticPoolName}: Current vCore setting {currentCpu} is at or below the floor {vCoreFloor}. Keeping at floor.");
                    return vCoreFloor;
                }

                // Otherwise, look up the correct next step.
                return vCoreOptions.ElementAt(currentIndex - 1);

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    /// <summary>
    /// Scales the specified HyperScale Elastic Pool to the desired vCore count.
    /// </summary>
    /// <param name="resourceGroupName">The resource group name.</param>
    /// <param name="serverName">The SQL Server name.</param>
    /// <param name="elasticPoolName">The Elastic Pool name.</param>
    /// <param name="newPoolSettings">The new pool settings.</param>
    private async Task ScaleElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName, PoolTargetSettings newPoolSettings)
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

            // If dry run is enabled, log the intended scaling action and return
            if (_config.IsDryRun)
            {
                _logger.LogWarning($"{elasticPoolName}: Dry run enabled. Would scale to {newPoolSettings.VCore} vCores.");
                return;
            }

            // Update the SKU to the desired vCore count
            var elasticPoolPatch = new ElasticPoolPatch
            {
                Sku = new SqlSku(elasticPool.Data.Sku.Name)
                {
                    Name = elasticPool.Data.Sku.Name,  // Preserve current SKU name
                    Tier = HyperScaleTier,             // Ensure tier is set to 'Hyperscale'
                    Capacity = (int)newPoolSettings.VCore   // Set the target vCore count as int
                },
                PerDatabaseSettings = new ElasticPoolPerDatabaseSettings
                {
                    MinCapacity = PoolTargetSettings.PerDbMinCapacity,
                    MaxCapacity = newPoolSettings.PerDbMaxCapacity
                }
            };

            // Apply the updated configuration.
            // Fire and forget the scaling operation. We're not going to wait around for it to finish.
            // It could take ~2 minutes, and we have other pools to scale.
            // We'll be checking on re-execution of this function whether any pools are in transition before we attempt
            // any other scaling operations on them.
            _ = await elasticPool.UpdateAsync(WaitUntil.Started, elasticPoolPatch).ConfigureAwait(false);

            _logger.LogWarning($"{elasticPoolName}: Scaling operation for elastic pool started.");
        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex, $"{elasticPoolName}: Failed to scale pool to {newPoolSettings.VCore} vCores.");
        }
    }

    /// <summary>
    /// Helper method to get the current Elastic Pool resource.
    /// </summary>
    private async Task<ElasticPoolResource?> GetElasticPoolAsync(string resourceGroupName, string serverName, string elasticPoolName)
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
    /// Check permissions to access SQL server and elastic pools
    /// </summary>
    /// <returns> </returns>
    private async Task<bool> CheckPermissionsAsync()
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
}