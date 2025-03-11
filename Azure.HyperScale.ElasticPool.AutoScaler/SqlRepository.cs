using Azure.Core;
using Azure.Identity;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;

namespace Azure.HyperScale.ElasticPool.AutoScaler;
public class SqlRepository(ILogger<SqlRepository> logger,
    AutoScalerConfiguration configuration,
    IErrorRecorder errorRecorder) : ISqlRepository
{
    private readonly ILogger<SqlRepository> _logger = logger;
    private readonly AutoScalerConfiguration _config = configuration;
    private readonly IErrorRecorder _errorRecorder = errorRecorder;

    private static string CreateSqlCompatibleList(List<string> poolsToConsider)
    {
        return string.Join(", ", poolsToConsider.Select(name => $"'{name}'"));
    }

    public async Task<IEnumerable<UsageInfo>?> SamplePoolMetricsAsync(List<string> poolsToConsider)
    {
        var elasticPoolNames = CreateSqlCompatibleList(poolsToConsider);
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
                await using var masterConnection = CreateSqlConnection(_config.MasterSqlConnection);
                var metricDbsToQuery = await masterConnection
                    .QueryAsync<(string DatabaseName, string ElasticPoolName)>(findPoolDatabasesForMetrics)
                    .ConfigureAwait(false);

                return await GetShortLongWindowUsageAsync(metricDbsToQuery).ConfigureAwait(false);
            });
        }
        catch (SqlException ex)
        {
            _errorRecorder.RecordError(ex, $"{_config.SqlInstanceName}: SamplePoolMetricsAsync() threw an error while measuring pool metrics.");
            return null;
        }
    }

    private AsyncRetryPolicy GetRetryPolicy()
    {
        var retryPolicy = Policy
            .Handle<SqlException>(ex => ex.IsTransient)
            .WaitAndRetryAsync(_config.RetryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(_config.RetryInterval, retryAttempt)));
        return retryPolicy;
    }

    private async Task<IEnumerable<UsageInfo>> GetShortLongWindowUsageAsync(IEnumerable<(string DatabaseName, string ElasticPoolName)> metricDbsToQuery)
    {
        var sql = $"""
        DECLARE @LongWindowStartTime DATETIME = DATEADD(SECOND, -{_config.LongWindowLookback}, GETUTCDATE());
        DECLARE @ShortWindowStartTime DATETIME = DATEADD(SECOND, -{_config.ShortWindowLookback}, GETUTCDATE());

        WITH PoolStats AS (
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
            end_time >= @LongWindowStartTime
        )
        SELECT
            (SELECT TOP 1 CAST(instance_vcores AS INT) FROM PoolStats ORDER BY end_time DESC) as ElasticPoolCpuLimit,
            ISNULL(AVG(CASE WHEN end_time >= @ShortWindowStartTime THEN avg_cpu_percent END), AVG(avg_cpu_percent)) AS ShortAvgCpu,
            AVG(avg_cpu_percent) AS LongAvgCpu,
            ISNULL(AVG(CASE WHEN end_time >= @ShortWindowStartTime THEN max_worker_percent END), AVG(max_worker_percent)) AS ShortWorkers,
            AVG(max_worker_percent) AS LongWorkers,
            ISNULL(AVG(CASE WHEN end_time >= @ShortWindowStartTime THEN avg_instance_cpu_percent END), AVG(avg_instance_cpu_percent)) AS ShortInstCpu,
            AVG(avg_instance_cpu_percent) AS LongInstCpu,
            ISNULL(AVG(CASE WHEN end_time >= @ShortWindowStartTime THEN avg_data_io_percent END), AVG(avg_data_io_percent)) AS ShortDataIo,
            AVG(avg_data_io_percent) AS LongDataIo
        FROM
            PoolStats;
        """;

        var tasks = metricDbsToQuery.Select(async db =>
        {
            try
            {
                var retryPolicy = GetRetryPolicy();

                return await retryPolicy.ExecuteAsync(async () =>
                {
                    await using var databaseConnection = CreateSqlConnection(_config.PoolDbConnection.Replace("{DatabaseName}", db.DatabaseName));
                    var statsRows = (await databaseConnection.QueryAsync<dynamic>(sql).ConfigureAwait(false)).ToList();

                    if (statsRows.Count == 0)
                    {
                        return null;
                    }

                    var row = statsRows.First();

                    return new UsageInfo
                    {
                        ElasticPoolName = db.ElasticPoolName,
                        ElasticPoolCpuLimit = row.ElasticPoolCpuLimit,
                        ShortAvgCpu = row.ShortAvgCpu,
                        LongAvgCpu = row.LongAvgCpu,
                        ShortWorkersPercent = row.ShortWorkers,
                        LongWorkersPercent = row.LongWorkers,
                        ShortInstanceCpu = row.ShortInstCpu,
                        LongInstanceCpu = row.LongInstCpu,
                        ShortDataIo = row.ShortDataIo,
                        LongDataIo = row.LongDataIo
                    };
                }).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                _errorRecorder.RecordError(ex, $"{db.ElasticPoolName}: Error fetching short and long window usage metrics.");
                return null;
            }
        });

        var metricsResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. metricsResults.Where(result => result != null).Select(result => result!)];
    }
       /// <summary>
    /// Retrieves the names of elastic pools that are currently in transition.
    /// </summary>
    /// <returns>A list of pools currently transitioning.</returns>
    public async Task<IEnumerable<string>?> GetPoolsInTransitionAsync()
    {
        var elasticPoolNames = CreateSqlCompatibleList(_config.ElasticPools.Keys.ToList());
        // Ensure we're only looking at the most recent UPDATE ELASTIC POOL entry
        // for each pool under consideration.

        // The states for pools in transition might be:
        // 0 = Pending
        // 1 = In progress
        // 4 = Cancel in progress
        // The states we're not concerned with:
        // 2 = Completed
        // 3 = Failed
        // 5 = Cancelled

        var sql = $"""
               WITH LatestOperations AS (
                    SELECT
                        major_resource_id AS [ElasticPoolInTransition],
                        state_desc AS [State],
                        DATEDIFF(SECOND, start_time, last_modify_time) AS [OperationDurationSeconds],
                        ROW_NUMBER() OVER (PARTITION BY major_resource_id ORDER BY last_modify_time DESC) AS rn
                    FROM sys.dm_operation_status
                    WHERE resource_type = 0 -- Database
                    AND operation = 'UPDATE ELASTIC POOL'
                    AND state IN (0, 1, 4)
                    AND major_resource_id IN ({elasticPoolNames})
               )
               SELECT
               ElasticPoolInTransition,
               State,
               OperationDurationSeconds
               FROM LatestOperations
               WHERE rn = 1;
               """;
        try
        {

            var retryPolicy = GetRetryPolicy();

            return await retryPolicy.ExecuteAsync(async () =>
            {
                await using var masterConnection = CreateSqlConnection(_config.MasterSqlConnection);
                var pools = (await masterConnection.QueryAsync<(string ElasticPoolInTransition, string State, int OperationDurationSeconds)>(sql)
                    .ConfigureAwait(false)).ToList();

                // Log each pool's state
                foreach (var pool in pools)
                {
                    _logger.LogInformation("Pool {PoolName} is in state {State}", pool.ElasticPoolInTransition, pool.State);

                    // Log a warning if any pool in transition is taking longer than expected
                    if (pool.State.ToUpper() == "IN PROGRESS" && pool.OperationDurationSeconds > _config.MaxExpectedScalingTimeSeconds)
                    {
                        _logger.LogWarning("Pool {PoolName} has been in transition for {DurationSeconds} seconds. This is longer than the configured expected max scaling time of {MaxExpectedScalingTimeSeconds}.",
                            pool.ElasticPoolInTransition, pool.OperationDurationSeconds, _config.MaxExpectedScalingTimeSeconds);
                    }
                }

                // Return only the pool names
                return pools.Select(p => p.ElasticPoolInTransition).ToList();
            }).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            // Log the exception with a descriptive message.
            _errorRecorder.RecordError(ex, $"{_config.SqlInstanceName}: Failed to retrieve elastic pool transition status from master database.");
            return null;
        }
    }

    /// <summary>
    /// Retrieves the number of seconds since the last completed scaling operation for each elastic pool
    /// we are monitoring. Queries sys.dm_operation_status for the last successfully completed scaling operations.
    /// Rows in sys.dm_operation_status only stick around for about 30 minutes. But that's long enough for our purposes.
    /// </summary>
    /// <returns>A dictionary of elastic pool names and the number of seconds since their last scaling operation.</returns>
    public async Task<Dictionary<string, int>?> GetLastScalingOperationsAsync()
    {
        var elasticPoolNames = CreateSqlCompatibleList(_config.ElasticPools.Keys.ToList());
        var sql = $"""
                   SELECT
                       major_resource_id AS [ElasticPoolName],
                       DATEDIFF(SECOND, MAX(last_modify_time), GETUTCDATE()) AS [SecondsSinceLastScaling]
                   FROM sys.dm_operation_status
                   WHERE resource_type = 0 -- Database
                   AND operation = 'UPDATE ELASTIC POOL'
                   AND state = 2 -- Completed
                   AND major_resource_id IN ({elasticPoolNames})
                   GROUP BY major_resource_id;
                   """;
        try
        {
            var retryPolicy = GetRetryPolicy();

            return await retryPolicy.ExecuteAsync(async () =>
            {
                await using var masterConnection = CreateSqlConnection(_config.MasterSqlConnection);
                var results = await masterConnection.QueryAsync<(string ElasticPoolName, int SecondsSinceLastScaling)>(sql).ConfigureAwait(false);

                var resultDict = results.ToDictionary(
                    p => p.ElasticPoolName,
                    p => p.SecondsSinceLastScaling
                );

                return resultDict;
            }).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            _errorRecorder.RecordError(ex, $"{_config.SqlInstanceName}: Failed to retrieve last scaling operations from master database.");
            return null;
        }
    }

    private SqlConnection CreateSqlConnection(string connectionString)
    {
        var sqlConnection = new SqlConnection(connectionString);

        if (AutoScalerConfiguration.IsUsingManagedIdentity)
        {
            sqlConnection.AccessToken = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = AutoScalerConfiguration.ManagedIdentityClientId
            }).GetToken(new TokenRequestContext(new[] { "https://database.windows.net/.default" })).Token;
        }
        return sqlConnection;
    }

    public async Task WriteToAutoScaleMonitorTable(UsageInfo elasticPool, double currentVCore, double targetVCore)
    {
        // If no connection string is set here, just return.
        if (_config.MetricsSqlConnection.Length == 0)
        {
            return;
        }

        try
        {
            await using var metricsConnection = CreateSqlConnection(_config.MetricsSqlConnection);
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
            _errorRecorder.RecordError(ex,
                $"{elasticPool.ElasticPoolName}: Error while writing metrics to AutoScalerMonitor table.");
        }
    }
}