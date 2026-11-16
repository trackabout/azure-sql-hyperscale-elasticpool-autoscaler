using Microsoft.Extensions.Logging;

namespace Azure.HyperScale.ElasticPool.AutoScaler
{
    /// <summary>
    /// Records an error once. When Sentry is configured, its logger integration turns each
    /// LogError into one Sentry event, so this class must not also capture to Sentry directly;
    /// doing so sent each failure two or three times.
    /// </summary>
    public class ErrorRecorder(ILogger<ErrorRecorder> logger) : IErrorRecorder
    {
        public void RecordError(Exception? ex, string message)
        {
            logger.LogError(ex, message);
        }

        public void RecordError(string message)
        {
            RecordError(null, message);
        }
    }
}
