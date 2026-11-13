using Microsoft.Extensions.Logging;

namespace Azure.HyperScale.ElasticPool.AutoScaler;

public interface IRunHeartbeat
{
    void RunCompleted(string sqlInstanceName);
}

/// <summary>
/// Logs one line per healthy run so an Azure Monitor alert can fire when the lines stop.
/// It has its own logger category because Program.cs lets Information through to
/// Application Insights for this category only; the worker sends Warning and above
/// for everything else. The host's "Executed 'Functions.AutoScaler' (Succeeded)" line
/// can't serve as a heartbeat: DoTheThing catches its own exceptions, so the host
/// reports success even when every run fails.
/// </summary>
public class RunHeartbeat(ILogger<RunHeartbeat> logger) : IRunHeartbeat
{
    public void RunCompleted(string sqlInstanceName)
    {
        logger.LogInformation("AutoScaler run completed for server {SqlInstanceName}", sqlInstanceName);
    }
}
