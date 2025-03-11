namespace Azure.HyperScale.ElasticPool.AutoScaler;

public interface ISqlRepository
{
    /// <summary>
    /// Retrieves usage metrics (CPU, IO, etc.) for the specified pools.
    /// </summary>
    Task<IEnumerable<UsageInfo>?> SamplePoolMetricsAsync(List<string> poolsToConsider);

    /// <summary>
    /// Identifies which pools are in the middle of a scaling operation (if any).
    /// </summary>
    Task<IEnumerable<string>> GetPoolsInTransitionAsync();

    /// <summary>
    /// Retrieves the last scaling operation for each pool.
    /// </summary>
    Task<Dictionary<string, int>?> GetLastScalingOperationsAsync();

    /// <summary>
    /// Writes the specified usage metrics to the AutoScaleMonitor table.
    /// </summary>
    Task WriteToAutoScaleMonitorTableAsync(UsageInfo elasticPool, double currentVCore, double targetVCore);

    /// <summary>
    /// Returns a list of elastic pools currently within the cooldown period.
    /// </summary>
    Task<IEnumerable<string>> GetPoolsInCoolDown();

    /// <summary>
    /// Determines the pools to consider for scaling by taking the basic list
    /// from configuration and excluding pools in transition or in cool down.
    /// </summary>
    /// <returns>A list of pools to consider for scaling.</returns>
    Task<List<string>> GetPoolsToConsider();
}