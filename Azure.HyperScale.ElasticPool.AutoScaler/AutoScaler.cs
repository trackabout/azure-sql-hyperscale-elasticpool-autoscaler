using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Sql.Models;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager;
using Dapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;

namespace Azure.HyperScale.ElasticPool.AutoScaler;

public enum SearchDirection
{
    Higher,
    Lower
}

public enum ScalingActions
{
    Up,
    Down,
    Hold
}

public class AutoScaler(
    ILogger<AutoScaler> logger,
    AutoScalerConfiguration autoScalerConfig)
{
    private const string HyperScaleTier = "Hyperscale";
    private const string SentryTagSqlInstanceName = "SqlInstanceName";

    public static bool IsUsingManagedIdentity => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"));

    public static string? AzureClientId => Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");

    [Function("AutoScaler")]
    // Can use HttpTrigger for testing the function. Hitting an HTTP trigger is a good way to test and debug.
    // Much easier than a Timer
    //public async Task Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "trigger")] HttpRequestData req)

    public async Task Run([TimerTrigger("*/15 * * * * *")] TimerInfo myTimer)
    {
        try
        {
            // Check permissions
            var hasPermissions = await CheckPermissionsAsync().ConfigureAwait(false);
            if (!hasPermissions)
            {
                logger.LogError("Insufficient permissions to access SQL server or elastic pools.");
                return;
            }

            // Check for pending scaling operations
            var poolsInTransition = await GetPoolsInTransitionAsync().ConfigureAwait(false);
            var poolsToConsider = autoScalerConfig.ElasticPools;

            // If there are ongoing operations for specific elastic pools, exclude them from the rest of this execution.
            if (poolsInTransition != null)
            {

                // Case-insensitive comparison of pool names, just in case we mistype them in the config.
                // Remove from consideration any pools that are in transition.
                var poolsInTransitionSet = new HashSet<string>(poolsInTransition, StringComparer.OrdinalIgnoreCase);
                poolsToConsider = autoScalerConfig.ElasticPools
                    .Where(pool => !poolsInTransitionSet.Contains(pool))
                    .ToList();
            }

            // If there are no pools to consider, log and return.
            if (poolsToConsider.Count == 0)
            {
                logger.LogInformation($"No pools to evaluate this time for server {autoScalerConfig.SqlInstanceName}.");
                return;
            }

            var poolMetrics = await SamplePoolMetricsAsync(poolsToConsider).ConfigureAwait(false);
            if (poolMetrics == null)
            {
                RecordError($"Unexpected: SamplePoolMetricsAsync() returned null while sampling pool metrics for server {autoScalerConfig.SqlInstanceName}.");
                return;
            }

            await CheckAndScalePoolsAsync(poolMetrics).ConfigureAwait(false);

        }
        catch (Exception ex)
        {
            RecordError(ex, "Unexpected error in AutoScaler.Run() function.");
        }
    }

    private async Task CheckAndScalePoolsAsync(IEnumerable<UsageInfo> poolMetrics)
    {
        // Loop through each pool and evaluate the metrics
        foreach (var metric in poolMetrics)
        {
            var serverAndPool = $"{autoScalerConfig.SqlInstanceName}.{metric.ElasticPoolName}";
            logger.LogInformation($"Evaluating pool {serverAndPool}");
            logger.LogInformation(metric.ToString());

            var currentVCore = (double)metric.ElasticPoolCpuLimit;

            // Figure out the new target vCore.
            var newPoolSettings = CalculatePoolTargetSettings(metric, currentVCore);

            // If the target vCore is the same as the current vCore, no scaling is necessary.
            if (newPoolSettings.VCore.Equals(currentVCore)) continue;

            // We are going to scale!
            try
            {
                logger.LogWarning($"{serverAndPool}: Scaling from {currentVCore} to {newPoolSettings.VCore}");
                await ScaleElasticPoolAsync(autoScalerConfig.ResourceGroupName,
                    autoScalerConfig.SqlInstanceName, metric.ElasticPoolName, newPoolSettings).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordError(ex,
                    $"{serverAndPool}: Error while scaling from {currentVCore} to {newPoolSettings.VCore}");
            }

            // Write current SLO to monitor table
            await WriteMetricsAsync(metric, currentVCore, newPoolSettings.VCore).ConfigureAwait(false);
        }
    }

    private async Task<IEnumerable<UsageInfo>?> SamplePoolMetricsAsync(List<string> poolsToConsider)
    {
        // Create a SQL compatible list of Elastic Pool Names
        var elasticPoolNames = string.Join("', '", poolsToConsider.Select(name => $"'{name}'"));

        // There is a view in the master database, sys.elastic_pool_resource_stats, which provides
        // metrics for all elastic pools on a given server, but its metrics are significantly delayed.
        // We've seen delays over 5 minutes.

        // The most timely source of metrics from which to base scaling decisions comes from
        // the view sys.dm_elastic_pool_resource_stats.

        // In order to make use of the most timely source, sys.dm_elastic_pool_resource_stats, we'll
        // need to pick a database in each pool of interest to query. Which one doesn't matter.

        var findPoolDatabasesForMetrics = $"""

                                            WITH PoolDatabases AS (
                                                SELECT
                                                    DatabaseName = d.name,
                                                    ElasticPoolName = dso.elastic_pool_name,
                                                    ROW_NUMBER() OVER (PARTITION BY dso.elastic_pool_name ORDER BY NEWID()) AS rn
                                                FROM
                                                    sys.database_service_objectives dso
                                                JOIN
                                                    sys.databases d ON d.database_id = dso.database_id
                                                WHERE
                                                    dso.elastic_pool_name IN ({elasticPoolNames})
                                                AND
                                                    d.state = 0 -- ONLINE
                                            )
                                            SELECT
                                                DatabaseName,
                                                ElasticPoolName
                                            FROM
                                                PoolDatabases
                                            WHERE
                                                rn = 1;
                                            """;
        try
        {
            var retryPolicy = GetRetryPolicy();
            return await retryPolicy.ExecuteAsync(async () =>
            {
                await using var masterConnection = CreateSqlConnection(autoScalerConfig.MasterSqlConnection);
                var metricDbsToQuery = await masterConnection
                    .QueryAsync<(string DatabaseName, string ElasticPoolName)>(findPoolDatabasesForMetrics)
                    .ConfigureAwait(false);

                return await GetPoolMetrics(metricDbsToQuery).ConfigureAwait(false);
            });
        }
        catch (SqlException ex)
        {
            RecordError(ex, $"{autoScalerConfig.SqlInstanceName}: SamplePoolMetricsAsync() threw an error while measuring pool metrics.");
            return null;
        }
    }

    private async Task<List<UsageInfo>> GetPoolMetrics(IEnumerable<(string DatabaseName, string ElasticPoolName)> metricDbsToQuery)
    {
        var hysteresisSql = $"""
                             -- Get Elastic Pool name from sys.database_service_objectives
                             DECLARE @ElasticPoolName NVARCHAR(128);

                             SELECT @ElasticPoolName = elastic_pool_name
                             FROM sys.database_service_objectives
                             WHERE elastic_pool_name IS NOT NULL;

                             -- Check if we retrieved the pool name
                             IF @ElasticPoolName IS NULL
                             BEGIN
                                 PRINT 'This database is not part of an elastic pool or the elastic pool name is not available.';
                                 RETURN;
                             END

                             -- Query CPU, worker, instance CPU, and data IO metrics with hysteresis scaling logic
                             ;WITH PoolStats AS (
                                 SELECT
                                     instance_vcores,
                                     end_time,
                                     avg_cpu_percent,
                                     max_worker_percent,
                                     avg_instance_cpu_percent,
                                     avg_data_io_percent,
                                     ROW_NUMBER() OVER (ORDER BY end_time DESC) AS RowNum
                                 FROM
                                     sys.dm_elastic_pool_resource_stats
                                 WHERE
                                     end_time >= DATEADD(SECOND, -{autoScalerConfig.LookBackSeconds}, GETUTCDATE())  -- Retrieve data from the last 15 minutes
                             ),

                             HighCpuStreak AS (
                                 SELECT
                                     COUNT(*) AS HighCpuCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastHighCpuTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     avg_cpu_percent >= {autoScalerConfig.HighCpuPercent}
                             ),

                             LowCpuStreak AS (
                                 SELECT
                                     COUNT(*) AS LowCpuCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastLowCpuTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     avg_cpu_percent <= {autoScalerConfig.LowCpuPercent}
                             ),

                             HighWorkerStreak AS (
                                 SELECT
                                     COUNT(*) AS HighWorkerCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastHighWorkerTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     max_worker_percent >= {autoScalerConfig.HighWorkersPercent}
                             ),

                             LowWorkerStreak AS (
                                 SELECT
                                     COUNT(*) AS LowWorkerCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastLowWorkerTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     max_worker_percent <= {autoScalerConfig.LowWorkersPercent}
                             ),

                             HighInstanceCpuStreak AS (
                                 SELECT
                                     COUNT(*) AS HighInstanceCpuCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastHighInstanceCpuTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     avg_instance_cpu_percent >= {autoScalerConfig.HighInstanceCpuPercent}
                             ),

                             LowInstanceCpuStreak AS (
                                 SELECT
                                     COUNT(*) AS LowInstanceCpuCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastLowInstanceCpuTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     avg_instance_cpu_percent <= {autoScalerConfig.LowInstanceCpuPercent}
                             ),

                             HighDataIoStreak AS (
                                 SELECT
                                     COUNT(*) AS HighDataIoCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastHighDataIoTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     avg_data_io_percent >= {autoScalerConfig.HighDataIoPercent}
                             ),

                             LowDataIoStreak AS (
                                 SELECT
                                     COUNT(*) AS LowDataIoCount,
                                     ISNULL(MAX(end_time), '1900-01-01') AS LastLowDataIoTime
                                 FROM
                                     PoolStats
                                 WHERE
                                     avg_data_io_percent <= {autoScalerConfig.LowDataIoPercent}
                             )

                             SELECT
                                 @ElasticPoolName AS ElasticPoolName,
                                 ps.instance_vcores as ElasticPoolCpuLimit,
                                 ISNULL(ps.end_time, '1900-01-01') as TimeStamp,
                                 ps.avg_cpu_percent as AvgCpuPercent,
                                 ps.max_worker_percent as WorkersPercent,
                                 ps.avg_instance_cpu_percent as AvgInstanceCpuPercent,
                                 ps.avg_data_io_percent as AvgDataIoPercent,
                                 hcs.HighCpuCount,
                                 ISNULL(hcs.LastHighCpuTime, '1900-01-01') as LastHighCpuTime,
                                 lcs.LowCpuCount,
                                 ISNULL(lcs.LastLowCpuTime, '1900-01-01') as LastLowCpuTime,
                                 hws.HighWorkerCount,
                                 ISNULL(hws.LastHighWorkerTime, '1900-01-01') as LastHighWorkerTime,
                                 lws.LowWorkerCount,
                                 ISNULL(lws.LastLowWorkerTime, '1900-01-01') as LastLowWorkerTime,
                                 his.HighInstanceCpuCount,
                                 ISNULL(his.LastHighInstanceCpuTime, '1900-01-01') as LastHighInstanceCpuTime,
                                 lis.LowInstanceCpuCount,
                                 ISNULL(lis.LastLowInstanceCpuTime, '1900-01-01') as LastLowInstanceCpuTime,
                                 hdis.HighDataIoCount,
                                 ISNULL(hdis.LastHighDataIoTime, '1900-01-01') as LastHighDataIoTime,
                                 ldis.LowDataIoCount,
                                 ISNULL(ldis.LastLowDataIoTime, '1900-01-01') as LastLowDataIoTime
                             FROM
                                 PoolStats ps
                             CROSS JOIN
                                 HighCpuStreak hcs
                             CROSS JOIN
                                 LowCpuStreak lcs
                             CROSS JOIN
                                 HighWorkerStreak hws
                             CROSS JOIN
                                 LowWorkerStreak lws
                             CROSS JOIN
                                 HighInstanceCpuStreak his
                             CROSS JOIN
                                 LowInstanceCpuStreak lis
                             CROSS JOIN
                                 HighDataIoStreak hdis
                             CROSS JOIN
                                 LowDataIoStreak ldis
                             WHERE
                                 ps.RowNum = 1;  -- Get the latest entry

                             """;
        var tasks = metricDbsToQuery.Select(async db =>
        {
            try
            {
                // Define the retry policy once outside the execution
                var retryPolicy = GetRetryPolicy();

                return await retryPolicy.ExecuteAsync(async () =>
                {
                    // Create and dispose the connection within the retry scope
                    await using var databaseConnection = CreateSqlConnection(autoScalerConfig.PoolDbConnection.Replace("{DatabaseName}", db.DatabaseName));
                    var metrics = (await databaseConnection.QueryAsync<UsageInfo>(hysteresisSql).ConfigureAwait(false)).ToList();

                    // ReSharper disable once PossibleMultipleEnumeration
                    if (metrics.Count == 0)
                    {
                        logger.LogWarning($"{db.ElasticPoolName}: No metrics rows were returned. Perhaps we are post-transition?");
                    }
                    // ReSharper disable once PossibleMultipleEnumeration
                    return metrics;
                }).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                RecordError(ex, $"{db.ElasticPoolName}: Error fetching metrics.");
                return Enumerable.Empty<UsageInfo>();
            }
        });

        var metricsResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        return metricsResults.SelectMany(m => m).ToList();
    }

    private static AsyncRetryPolicy GetRetryPolicy()
    {
        var retryPolicy = Policy
            .Handle<SqlException>(ex => ex.IsTransient)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
        return retryPolicy;
    }

    /// <summary>
    /// Retrieves the names of elastic pools that are currently in transition.
    /// </summary>
    /// <returns>A list of pools currently transitioning.</returns>
    private async Task<IEnumerable<string>?> GetPoolsInTransitionAsync()
    {
        const string sql = """
                           SELECT major_resource_id AS [ElasticPoolInTransition], state_desc AS [State]
                           FROM sys.dm_operation_status
                           WHERE resource_type = 0 -- Database
                           AND operation = 'UPDATE ELASTIC POOL'
                           AND state IN (0, 1, 4)
                           """;
        try
        {
            // The states for pools in transition might be:
            // 0 = Pending
            // 1 = In progress
            // 4 = Cancel in progress
            // The states we're not concerned with:
            // 2 = Completed
            // 3 = Failed
            // 5 = Cancelled

            var retryPolicy = GetRetryPolicy();

            return await retryPolicy.ExecuteAsync(async () =>
            {
                await using var masterConnection = CreateSqlConnection(autoScalerConfig.MasterSqlConnection);
                var pools = (await masterConnection.QueryAsync<(string ElasticPoolInTransition, string State)>(sql)
                    .ConfigureAwait(false)).ToList();

                // Log each pool's state
                foreach (var pool in pools)
                {
                    logger.LogInformation("Pool {PoolName} is in state {State}", pool.ElasticPoolInTransition, pool.State);
                }

                // Return only the pool names
                return pools.Select(p => p.ElasticPoolInTransition).ToList();
            }).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            // Log the exception with a descriptive message.
            RecordError(ex, $"{autoScalerConfig.SqlInstanceName}: Failed to retrieve elastic pool transition status from master database.");
            return null;
        }
    }


    public class PoolTargetSettings(double targetVCore, double perDbMaxCapacity)
    {
        public double VCore { get; } = targetVCore;

        public const double PerDbMinCapacity = 0;

        public double PerDbMaxCapacity => perDbMaxCapacity;
    }

    public double GetPerDatabaseMaximumByIndex(double targetVCore)
    {
        // Look up the array index of the targetVCore
        var index = autoScalerConfig.VCoreOptions.ToList().IndexOf(targetVCore);
        // Fetch the element from the PerDatabaseMaximums array at the same index
        return autoScalerConfig.PerDatabaseMaximums.ElementAt(index);
    }

    public PoolTargetSettings CalculatePoolTargetSettings(UsageInfo usageInfo, double currentVCore)
    {
        var targetVCore = currentVCore;
        var perDbMaxCapacity = GetPerDatabaseMaximumByIndex(currentVCore);

        var scalingAction = GetScalingAction(usageInfo);

        switch (scalingAction)
        {
            case ScalingActions.Up:
                targetVCore = GetServiceLevelObjective(currentVCore, ScalingActions.Up, usageInfo.ElasticPoolName);
                perDbMaxCapacity = GetPerDatabaseMaximumByIndex(targetVCore);
                if (targetVCore > currentVCore)
                {
                    logger.LogWarning($"{usageInfo.ElasticPoolName}: HIGH threshold reached. Will scale UP from {currentVCore} to {targetVCore}");
                }
                break;
            case ScalingActions.Down:
                targetVCore = GetServiceLevelObjective(currentVCore, ScalingActions.Down, usageInfo.ElasticPoolName);
                perDbMaxCapacity = GetPerDatabaseMaximumByIndex(targetVCore);
                if (targetVCore < currentVCore)
                {
                    logger.LogWarning($"{usageInfo.ElasticPoolName}: LOW threshold reached. Will scale DOWN from {currentVCore} to {targetVCore}");
                }
                break;

            case ScalingActions.Hold:
                logger.LogInformation($"{usageInfo.ElasticPoolName}: HOLD Current vCore: {currentVCore}. No change required.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scalingAction));
        }

        return new PoolTargetSettings(targetVCore, perDbMaxCapacity);
    }

    private ScalingActions GetScalingAction(UsageInfo pool)
    {
        // If any one of the high thresholds are met, scale up.
        if (pool.HighCpuCount >= autoScalerConfig.HighCountThreshold ||
            pool.HighWorkerCount >= autoScalerConfig.HighCountThreshold ||
            pool.HighInstanceCpuCount >= autoScalerConfig.HighCountThreshold ||
            pool.HighDataIoCount >= autoScalerConfig.HighCountThreshold)
        {
            return ScalingActions.Up;
        }

        // If ALL of the low thresholds are met, scale down.
        if (pool.LowCpuCount >= autoScalerConfig.LowCountThreshold &&
            pool.LowWorkerCount >= autoScalerConfig.LowCountThreshold &&
            pool.LowInstanceCpuCount >= autoScalerConfig.LowCountThreshold &&
            pool.LowDataIoCount >= autoScalerConfig.LowCountThreshold)
        {
            return ScalingActions.Down;
        }

        return ScalingActions.Hold;
    }

    private async Task WriteMetricsAsync(UsageInfo elasticPool, double currentVCore, double targetVCore)
    {
        WriteMetricsToLog(elasticPool, currentVCore, targetVCore);

        // If no connection string is set here, just return.
        if (autoScalerConfig.MetricsSqlConnection.Length == 0)
        {
            return;
        }

        try
        {
            await using var metricsConnection = CreateSqlConnection(autoScalerConfig.MetricsSqlConnection);
            await metricsConnection.ExecuteAsync(
                "INSERT INTO [hs].[AutoScalerMonitor] (ElasticPoolName, CurrentSLO, RequestedSLO, UsageInfo) " +
                "VALUES (@ElasticPoolName, @CurrentSLO, @RequestedSLO, @UsageInfo)",
                new
                {
                    elasticPool.ElasticPoolName,
                    CurrentSLO = currentVCore.ToString("F2"), // Format to 2 decimal places
                    RequestedSLO = targetVCore.ToString("F2"), // Format to 2 decimal places
                    UsageInfo = JsonConvert.SerializeObject(elasticPool)
                }).ConfigureAwait(false);

        }
        catch (SqlException ex)
        {
            RecordError(ex,
                $"{elasticPool.ElasticPoolName}: Error while writing metrics to AutoScalerMonitor table.");
        }
    }

    private void WriteMetricsToLog(UsageInfo usageInfo, double currentVCore, double targetVCore)
    {
        FunctionsLoggerExtensions.LogMetric(logger, "AvgCpuPercent", Convert.ToDouble(usageInfo.AvgCpuPercent));
        FunctionsLoggerExtensions.LogMetric(logger, "HighCpuCount", Convert.ToDouble(usageInfo.HighCpuCount));
        FunctionsLoggerExtensions.LogMetric(logger, "LowCpuCount", Convert.ToDouble(usageInfo.LowCpuCount));
        FunctionsLoggerExtensions.LogMetric(logger, "WorkersPercent", Convert.ToDouble(usageInfo.WorkersPercent));
        FunctionsLoggerExtensions.LogMetric(logger, "HighWorkerCount", Convert.ToDouble(usageInfo.HighWorkerCount));
        FunctionsLoggerExtensions.LogMetric(logger, "LowWorkerCount", Convert.ToDouble(usageInfo.LowWorkerCount));
        FunctionsLoggerExtensions.LogMetric(logger, "AvgInstanceCpuPercent", Convert.ToDouble(usageInfo.AvgInstanceCpuPercent));
        FunctionsLoggerExtensions.LogMetric(logger, "HighInstanceCpuCount", Convert.ToDouble(usageInfo.HighInstanceCpuCount));
        FunctionsLoggerExtensions.LogMetric(logger, "LowInstanceCpuCount", Convert.ToDouble(usageInfo.LowInstanceCpuCount));
        FunctionsLoggerExtensions.LogMetric(logger, "AvgDataIoPercent", Convert.ToDouble(usageInfo.AvgDataIoPercent));
        FunctionsLoggerExtensions.LogMetric(logger, "HighDataIoCount", Convert.ToDouble(usageInfo.HighDataIoCount));
        FunctionsLoggerExtensions.LogMetric(logger, "LowDataIoCount", Convert.ToDouble(usageInfo.LowDataIoCount));

        FunctionsLoggerExtensions.LogMetric(logger, "CurrentCpuLimit", currentVCore);
        FunctionsLoggerExtensions.LogMetric(logger, "TargetCpuLimit", targetVCore);
    }

    public double GetServiceLevelObjective(double currentCpu, ScalingActions action, string elasticPoolName)
    {
        var vCoreOptions = autoScalerConfig.VCoreOptions;
        var currentIndex = Array.IndexOf(vCoreOptions.ToArray(), currentCpu);

        // If currentCpu is not found in the array (this would be unusual), return currentCpu.
        if (currentIndex == -1)
        {
            RecordError($"{elasticPoolName}: Current vCore setting of {currentCpu} not found in the list of vCore options.");
            return currentCpu;
        }

        switch (action)
        {
            case ScalingActions.Hold:
                return currentCpu;

            case ScalingActions.Up:
                // If we're below the floor, raise to the floor.
                if (currentCpu < autoScalerConfig.VCoreFloor)
                {
                    logger.LogWarning($"{elasticPoolName}: Current vCore setting {currentCpu} is below the floor {autoScalerConfig.VCoreFloor}. Raising to floor.");
                    return autoScalerConfig.VCoreFloor;
                }

                // If we're at or above the ceiling, hold at ceiling.
                if (currentCpu >= autoScalerConfig.VCoreCeiling)
                {
                    logger.LogWarning($"{elasticPoolName}: Current vCore setting {currentCpu} is at or above the ceiling {autoScalerConfig.VCoreCeiling}. Keeping at ceiling.");
                    return autoScalerConfig.VCoreCeiling;
                }

                // Otherwise, look up the correct next step.
                return vCoreOptions.ElementAt(currentIndex + 1);

            case ScalingActions.Down:
                // If we're above the ceiling, bring down to ceiling.
                if (currentCpu > autoScalerConfig.VCoreCeiling)
                {
                    logger.LogWarning($"{elasticPoolName}: Current vCore setting {currentCpu} is above the ceiling {autoScalerConfig.VCoreCeiling}. Lowering to ceiling.");
                    return autoScalerConfig.VCoreCeiling;
                }

                // If we're at or below the floor, hold at the floor.
                if (currentCpu <= autoScalerConfig.VCoreFloor)
                {
                    logger.LogInformation($"{elasticPoolName}: Current vCore setting {currentCpu} is at or below the floor {autoScalerConfig.VCoreFloor}. Keeping at floor.");
                    return autoScalerConfig.VCoreFloor;
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
                RecordError($"Elastic Pool Azure resource '{elasticPoolName}' not found.");
                return;
            }

            // Check if the Elastic Pool is already at the desired vCore count
            if (elasticPool.Data.Sku.Capacity == (int)newPoolSettings.VCore)
            {
                RecordError($"{elasticPoolName}: Pool is already at {newPoolSettings.VCore} vCores. Nothing to do.");
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

            logger.LogWarning($"{elasticPoolName}: Scaling operation for elastic pool started.");
        }
        catch (Exception ex)
        {
            RecordError(ex, $"{elasticPoolName}: Failed to scale pool to {newPoolSettings.VCore} vCores.");
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
            var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{autoScalerConfig.SubscriptionId}"));
            var resourceGroup = await subscription.GetResourceGroups().GetAsync(resourceGroupName).ConfigureAwait(false);
            var sqlServer = await resourceGroup.Value.GetSqlServers().GetAsync(serverName).ConfigureAwait(false);
            return await sqlServer.Value.GetElasticPools().GetAsync(elasticPoolName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordError(ex, $"Failed to get Azure Resource Elastic Pool '{elasticPoolName}' in server '{serverName}' and resource group '{resourceGroupName}'.");
            return null;
        }
    }

    private void RecordError(Exception ex, string message)
    {
        logger.LogError(ex, message);

        if (autoScalerConfig.IsSentryLoggingEnabled)
        {
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag(SentryTagSqlInstanceName, autoScalerConfig.SqlInstanceName);
            });
            SentrySdk.CaptureException(ex);
            SentrySdk.CaptureMessage(message, SentryLevel.Error);
        }
    }

    private void RecordError(string message)
    {
        logger.LogError(message);

        if (autoScalerConfig.IsSentryLoggingEnabled)
        {
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag(SentryTagSqlInstanceName, autoScalerConfig.SqlInstanceName);
            });
            SentrySdk.CaptureMessage(message, SentryLevel.Error);
        }
    }

    private static SqlConnection CreateSqlConnection(string connectionString)
    {
        var sqlConnection = new SqlConnection(connectionString);

        if (IsUsingManagedIdentity)
        {
            sqlConnection.AccessToken = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = AzureClientId
            }).GetToken(new TokenRequestContext(new[] { "https://database.windows.net/.default" })).Token;
        }
        return sqlConnection;
    }

    // Check permissions to access SQL server and elastic pools
    private async Task<bool> CheckPermissionsAsync()
    {
        try
        {
            var elasticPools = autoScalerConfig.ElasticPools;
            foreach (var pool in elasticPools)
            {
                var elasticPool = await GetElasticPoolAsync(autoScalerConfig.ResourceGroupName, autoScalerConfig.SqlInstanceName, pool).ConfigureAwait(false);
                if (elasticPool == null)
                {
                    logger.LogError($"Failed to access elastic pool: {pool}");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            RecordError(ex, "Error while checking permissions to access SQL server or elastic pools.");
            return false;
        }
    }

}