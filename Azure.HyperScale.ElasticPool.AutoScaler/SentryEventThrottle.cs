using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

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
    public const string WindowSettingName = "SentryRepeatWindowSeconds";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

    private readonly record struct Entry(DateTimeOffset SentAt, DateTimeOffset LastSeen, int Dropped);

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Lock _lock = new();

    public int TrackedErrorCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Reads the repeat window from the <see cref="WindowSettingName"/> setting, in seconds.
    /// Defaults to 15 minutes. Zero means no throttling.
    /// </summary>
    public static TimeSpan WindowFromConfiguration(IConfiguration configuration)
    {
        var value = configuration[WindowSettingName];
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultWindow;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException(
                $"{WindowSettingName} must be a whole number of seconds, 0 or greater. 0 turns throttling off. The value was '{value}'.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public SentryEvent? Filter(SentryEvent sentryEvent)
    {
        var key = KeyFor(sentryEvent);
        var now = timeProvider.GetUtcNow();

        lock (_lock)
        {
            var seenBefore = _entries.TryGetValue(key, out var last);
            ForgetErrorsNotSeenSince(now - window);

            if (seenBefore && now - last.SentAt < window)
            {
                _entries[key] = last with { LastSeen = now, Dropped = last.Dropped + 1 };
                return null;
            }

            if (seenBefore && last.Dropped > 0)
            {
                sentryEvent.SetExtra(DroppedRepeatsKey, last.Dropped);
            }

            _entries[key] = new Entry(SentAt: now, LastSeen: now, Dropped: 0);
            return sentryEvent;
        }
    }

    // Without this the dictionary would grow for the life of the process: BeforeSend sees
    // every event, including library errors whose text changes on every occurrence.
    private void ForgetErrorsNotSeenSince(DateTimeOffset cutoff)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastSeen <= cutoff)
            {
                // Removing during enumeration is allowed for Dictionary since .NET Core 3.0.
                _entries.Remove(key);
            }
        }
    }

    private static string KeyFor(SentryEvent sentryEvent)
    {
        var exception = sentryEvent.Exception;
        // The rendered text comes first: a log call with placeholders shares one template
        // across every pool, and a failure in a second pool is not a repeat of the first.
        var message = sentryEvent.Message?.Formatted ?? sentryEvent.Message?.Message;
        return $"{exception?.GetType().FullName}|{CauseOf(exception)}|{message}";
    }

    // Tells apart two failures that share an exception type and a log message. It can't be the
    // exception's message: Azure puts a request ID in every one, so no two events would match.
    private static string? CauseOf(Exception? exception)
    {
        return exception switch
        {
            null => null,
            RequestFailedException azureError => $"{azureError.Status} {azureError.ErrorCode}",
            SqlException sqlError => sqlError.Number.ToString(CultureInfo.InvariantCulture),
            _ => WhereThrown(exception)
        };
    }

    private static string? WhereThrown(Exception exception)
    {
        var method = exception.TargetSite;
        return method == null ? null : $"{method.DeclaringType?.FullName}.{method.Name}";
    }
}
