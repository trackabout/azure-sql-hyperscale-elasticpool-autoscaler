using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
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

    private const string LookupFailed = "Failed to get elastic pool.";

    private static Exception ThrownFromOneMethod()
    {
        try { throw new InvalidOperationException("simulated failure"); }
        catch (Exception ex) { return ex; }
    }

    private static Exception ThrownFromAnotherMethod()
    {
        try { throw new InvalidOperationException("simulated failure"); }
        catch (Exception ex) { return ex; }
    }

    // SqlException has no public constructor, so build one the way the driver does.
    private static SqlException MakeSqlException(int number)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var errorConstructor = typeof(SqlError).GetConstructors(flags)
            .First(c => c.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(int));
        var arguments = errorConstructor.GetParameters()
            .Select((parameter, index) => index == 0 ? number : DefaultFor(parameter))
            .ToArray();
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [errorConstructor.Invoke(arguments)]);
        var create = typeof(SqlException).GetMethods(flags)
            .First(m => m.Name == "CreateException" && m.GetParameters().Length == 2);
        return (SqlException)create.Invoke(null, [errors, "16.0.0"])!;

        static object? DefaultFor(ParameterInfo parameter)
        {
            if (parameter.ParameterType == typeof(string)) return "simulated";
            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }
    }

    [Fact]
    public void AzureErrors_WithDifferentStatus_AreThrottledSeparately()
    {
        // The pool lookup logs the same text whether Azure answers "forbidden" or
        // "too many requests". A change of cause must still reach Sentry.
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent(LookupFailed, new RequestFailedException(403, "Forbidden")));

        var result = throttle.Filter(MakeEvent(LookupFailed, new RequestFailedException(429, "Too many requests")));

        Assert.NotNull(result);
    }

    [Fact]
    public void AzureErrors_DifferingOnlyByRequestId_AreThrottledTogether()
    {
        // Azure puts a request ID in every error message, so the message text can't be part of the comparison.
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent(LookupFailed, new RequestFailedException(403, "Forbidden. RequestId: 00000000-0000-0000-0000-000000000001")));

        var result = throttle.Filter(MakeEvent(LookupFailed, new RequestFailedException(403, "Forbidden. RequestId: 00000000-0000-0000-0000-000000000002")));

        Assert.Null(result);
    }

    [Fact]
    public void SqlErrors_WithDifferentNumbers_AreThrottledSeparately()
    {
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent("Metrics query failed.", MakeSqlException(18456)));

        var result = throttle.Filter(MakeEvent("Metrics query failed.", MakeSqlException(40613)));

        Assert.NotNull(result);
    }

    [Fact]
    public void SqlErrors_WithTheSameNumber_AreThrottledTogether()
    {
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent("Metrics query failed.", MakeSqlException(18456)));

        var result = throttle.Filter(MakeEvent("Metrics query failed.", MakeSqlException(18456)));

        Assert.Null(result);
    }

    [Fact]
    public void SameExceptionType_ThrownFromDifferentMethods_AreThrottledSeparately()
    {
        // The run's catch-all logs one fixed message, so two unrelated bugs that throw
        // the same exception type differ only in where they were thrown.
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent("Unexpected error.", ThrownFromOneMethod()));

        var result = throttle.Filter(MakeEvent("Unexpected error.", ThrownFromAnotherMethod()));

        Assert.NotNull(result);
    }

    [Fact]
    public void SameExceptionType_ThrownFromTheSameMethod_AreThrottledTogether()
    {
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(MakeEvent("Unexpected error.", ThrownFromOneMethod()));

        var result = throttle.Filter(MakeEvent("Unexpected error.", ThrownFromOneMethod()));

        Assert.Null(result);
    }

    [Fact]
    public void SameTemplate_DifferentRenderedText_AreThrottledSeparately()
    {
        // A log call with placeholders gives Sentry both the template and the rendered text.
        var throttle = new SentryEventThrottle(Window, new ManualTimeProvider());
        throttle.Filter(new SentryEvent
        {
            Message = new SentryMessage { Message = "Pool {Pool} failed", Formatted = "Pool example-pool-1 failed" }
        });

        var result = throttle.Filter(new SentryEvent
        {
            Message = new SentryMessage { Message = "Pool {Pool} failed", Formatted = "Pool example-pool-2 failed" }
        });

        Assert.NotNull(result);
    }

    [Fact]
    public void ErrorNotSeenForAFullWindow_IsForgotten()
    {
        var clock = new ManualTimeProvider();
        var throttle = new SentryEventThrottle(Window, clock);
        throttle.Filter(MakeEvent("pool unreachable"));
        Assert.Equal(1, throttle.TrackedErrorCount);

        clock.Now += Window;
        throttle.Filter(MakeEvent("scaling failed"));

        Assert.Equal(1, throttle.TrackedErrorCount);
    }

    [Fact]
    public void ErrorThatKeepsRepeating_IsNotForgotten_AndKeepsItsDroppedCount()
    {
        var clock = new ManualTimeProvider();
        var throttle = new SentryEventThrottle(Window, clock);
        throttle.Filter(MakeEvent("pool unreachable"));

        // Repeats every 10 minutes: always inside a window of the last repeat.
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Null(throttle.Filter(MakeEvent("pool unreachable")));
        clock.Now += TimeSpan.FromMinutes(10);
        var result = throttle.Filter(MakeEvent("pool unreachable"));

        Assert.NotNull(result);
        Assert.Equal(1, result.Extra[SentryEventThrottle.DroppedRepeatsKey]);
    }

    private static IConfiguration ConfigurationWith(string? windowSeconds)
    {
        var settings = new Dictionary<string, string?>();
        if (windowSeconds != null) settings[SentryEventThrottle.WindowSettingName] = windowSeconds;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void WindowFromConfiguration_NotSet_DefaultsToFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), SentryEventThrottle.WindowFromConfiguration(ConfigurationWith(null)));
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("0", 0)]
    public void WindowFromConfiguration_ReadsSeconds(string value, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SentryEventThrottle.WindowFromConfiguration(ConfigurationWith(value)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("fifteen")]
    public void WindowFromConfiguration_InvalidValue_Throws(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SentryEventThrottle.WindowFromConfiguration(ConfigurationWith(value)));

        Assert.Contains(SentryEventThrottle.WindowSettingName, exception.Message);
    }

    [Fact]
    public void ZeroWindow_SendsEveryEvent()
    {
        var throttle = new SentryEventThrottle(TimeSpan.Zero, new ManualTimeProvider());
        throttle.Filter(MakeEvent("pool unreachable"));

        var result = throttle.Filter(MakeEvent("pool unreachable"));

        Assert.NotNull(result);
    }
}
