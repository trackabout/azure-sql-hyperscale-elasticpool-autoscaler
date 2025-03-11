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
    IErrorRecorder errorRecorder,
    IAzureResourceService azureResourceService)
{

    private IErrorRecorder _errorRecorder = errorRecorder ?? throw new ArgumentNullException(nameof(errorRecorder));
    private ISqlRepository _sqlRepository = sqlRepository ?? throw new ArgumentNullException(nameof(sqlRepository));
    private ILogger<AutoScaler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private AutoScalerConfiguration _config = autoScalerConfig ?? throw new ArgumentNullException(nameof(autoScalerConfig));
    private IAzureResourceService _azureResourceService = azureResourceService ?? throw new ArgumentNullException(nameof(azureResourceService));

    [Function("AutoScaler")]
    public async Task Run([TimerTrigger("*/15 * * * * *")] TimerInfo myTimer)
    {
        _ = await DoTheThing().ConfigureAwait(false);
    }

    /// <summary>
    /// Main function that does the scaling evaluation and scaling.
    /// </summary>
    /// <returns></returns>
    public async Task<bool> DoTheThing()
    {
        try
        {
            _logger.LogInformation("==================================================================================");
            _logger.LogInformation("--------------------------------- AutoScaler Run ---------------------------------");

            // Check permissions
            var hasPermissions = await _azureResourceService.CheckPermissionsAsync().ConfigureAwait(false);
            if (!hasPermissions)
            {
                _logger.LogError("Insufficient permissions to access elastic pools.");
                return false;
            }

            var poolsToConsider = await _sqlRepository.GetPoolsToConsider().ConfigureAwait(false);
            if (poolsToConsider.Count == 0)
            {
                _logger.LogInformation($"No pools to left to evaluate for server {_config.SqlInstanceName}.");
                return false;
            }

            var poolMetrics = await _sqlRepository.SamplePoolMetricsAsync(poolsToConsider).ConfigureAwait(false);
            if (poolMetrics == null)
            {
                _errorRecorder.RecordError($"Unexpected: SamplePoolMetricsAsync() returned null while sampling pool metrics for server {_config.SqlInstanceName}.");
                return false;
            }

            // Loop through each pool and evaluate the metrics
            foreach (var usageInfo in poolMetrics)
            {
                await EvaluateMetrics(usageInfo).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex, "Unexpected error in AutoScaler.");
        }

        return true;
    }

    private async Task EvaluateMetrics(UsageInfo usageInfo)
    {
        var serverAndPool = $"{_config.SqlInstanceName}.{usageInfo.ElasticPoolName}";
        _logger.LogInformation($"\n                 --=> Evaluating Pool {serverAndPool} <=--");
        _logger.LogInformation(usageInfo.ToString());

        var currentVCore = (double)usageInfo.ElasticPoolCpuLimit;

        // Figure out the new target vCore.
        var newPoolSettings = GetNewPoolTarget(usageInfo, currentVCore);

        // If the target vCore is the same as the current vCore, no scaling is necessary.
        if (newPoolSettings.VCore.Equals(currentVCore)) return;

        if (_config.IsDryRun)
        {
            _logger.LogWarning($"DRY RUN ENABLED: Would have scaled {serverAndPool} from {currentVCore} to {newPoolSettings.VCore}");
            return;
        }

        try
        {
            // We are going to scale!
            _logger.LogWarning($"ACTION!: Scaling {serverAndPool} from {currentVCore} to {newPoolSettings.VCore}");

            await _azureResourceService.ScaleElasticPoolAsync(_config.ResourceGroupName,
                _config.SqlInstanceName, usageInfo.ElasticPoolName, newPoolSettings, usageInfo, currentVCore).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _errorRecorder.RecordError(ex,
                $"{serverAndPool}: Error while scaling from {currentVCore} to {newPoolSettings.VCore}");
        }
    }

    public PoolTargetSettings GetNewPoolTarget(UsageInfo usageInfo, double currentVCore)
    {
        var targetVCore = currentVCore;
        var perDbMaxCapacity = _config.GetPerDatabaseMaxByVCore(currentVCore);
        var scalingAction = GetScalingAction(usageInfo);

        switch (scalingAction)
        {
            case ScalingActions.Up:
                targetVCore = GetServiceLevelObjective(currentVCore, ScalingActions.Up, usageInfo.ElasticPoolName);
                perDbMaxCapacity = _config.GetPerDatabaseMaxByVCore(targetVCore);
                if (targetVCore > currentVCore)
                {
                    _logger.LogWarning($"EVALUATION RESULT: HIGH threshold crossed.");
                }
                break;

            case ScalingActions.Down:
                targetVCore = GetServiceLevelObjective(currentVCore, ScalingActions.Down, usageInfo.ElasticPoolName);
                perDbMaxCapacity = _config.GetPerDatabaseMaxByVCore(targetVCore);
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

    private ScalingActions GetScalingAction(UsageInfo usageInfo)
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

    private double GetServiceLevelObjective(double currentCpu, ScalingActions action, string elasticPoolName)
    {
        var vCoreOptions = _config.VCoreOptions;
        var currentIndex = Array.IndexOf([.. vCoreOptions], currentCpu);

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
}