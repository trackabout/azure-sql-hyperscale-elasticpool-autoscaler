namespace Azure.HyperScale.ElasticPool.AutoScaler;

/// <summary>
/// Sentry BeforeSend filter that sends the first occurrence of an error, then drops
/// repeats of the same error for <c>window</c>. The function runs every 15 seconds, so
/// without this a persistent fault sends thousands of identical events a day and
/// can exhaust the Sentry quota. The first event sent after
/// a quiet window carries the number of repeats that were dropped.
/// </summary>
public class SentryEventThrottle(TimeSpan window, TimeProvider timeProvider)
{
    public const string DroppedRepeatsKey = "dropped_repeats";

    private readonly Dictionary<string, (DateTimeOffset SentAt, int Dropped)> _lastSent = new();
    private readonly Lock _lock = new();

    public SentryEvent? Filter(SentryEvent sentryEvent)
    {
        var key = KeyFor(sentryEvent);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_lastSent.TryGetValue(key, out var last) && now - last.SentAt < window)
            {
                _lastSent[key] = (last.SentAt, last.Dropped + 1);
                return null;
            }

            if (last.Dropped > 0)
            {
                sentryEvent.SetExtra(DroppedRepeatsKey, last.Dropped);
            }

            _lastSent[key] = (now, 0);
            return sentryEvent;
        }
    }

    private static string KeyFor(SentryEvent sentryEvent)
    {
        var exceptionType = sentryEvent.Exception?.GetType().FullName;
        var message = sentryEvent.Message?.Message ?? sentryEvent.Message?.Formatted;
        return $"{exceptionType}|{message}";
    }
}
