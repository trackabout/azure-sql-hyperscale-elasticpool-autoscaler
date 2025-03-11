using Microsoft.Extensions.Logging;

namespace Azure.HyperScale.ElasticPool.AutoScaler
{
    public class ErrorRecorder(ILogger<ErrorRecorder> logger, AutoScalerConfiguration autoScalerConfig) : IErrorRecorder
    {
        private readonly ILogger<ErrorRecorder> logger = logger;
        private readonly AutoScalerConfiguration autoScalerConfig = autoScalerConfig;
        private const string SentryTagSqlInstanceName = "SqlInstanceName";

        public void RecordError(Exception ex, string message)
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

        public void RecordError(string message)
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
    }
}