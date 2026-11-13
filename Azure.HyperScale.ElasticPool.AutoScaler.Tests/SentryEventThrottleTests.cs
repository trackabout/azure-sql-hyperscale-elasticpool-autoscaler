using Sentry;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class SentryEventThrottleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SentryEvent MakeEvent(string message, Exception? exception = null)
    {
        return new SentryEvent(exception) { Message = new SentryMessage { Message = message } };
    }

    [Fact]
    public void FirstOccurrence_IsSent()
    {
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());

        var result = throttle.Filter(MakeEvent("pool unreachable"));

        Assert.NotNull(result);
    }

    [Fact]
    public void RepeatWithinWindow_IsDropped()
    {
        var clock = new ManualTimeProvider();
        var throttle = new SentryEventThrottle(Window, clock);
        throttle.Filter(MakeEvent("pool unreachable"));

        clock.Now += TimeSpan.FromMinutes(14);
        var result = throttle.Filter(MakeEvent("pool unreachable"));

        Assert.Null(result);
    }

    [Fact]
    public void RepeatAfterWindow_IsSent_WithCountOfDroppedRepeats()
    {
        var clock = new ManualTimeProvider();
        var throttle = new SentryEventThrottle(Window, clock);
        throttle.Filter(MakeEvent("pool unreachable"));
        for (var i = 0; i < 3; i++)
        {
            clock.Now += TimeSpan.FromSeconds(15);
            throttle.Filter(MakeEvent("pool unreachable"));
        }

        clock.Now += Window;
        var result = throttle.Filter(MakeEvent("pool unreachable"));

        Assert.NotNull(result);
        Assert.Equal(3, result.Extra[SentryEventThrottle.DroppedRepeatsKey]);
    }

    [Fact]
    public void DifferentMessages_AreThrottledSeparately()
    {
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent("pool unreachable"));

        var result = throttle.Filter(MakeEvent("scaling failed"));

        Assert.NotNull(result);
    }

    [Fact]
    public void SameMessage_DifferentExceptionTypes_AreThrottledSeparately()
    {
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent("pool unreachable", new TimeoutException()));

        var result = throttle.Filter(MakeEvent("pool unreachable", new FileNotFoundException()));

        Assert.NotNull(result);
    }
}
