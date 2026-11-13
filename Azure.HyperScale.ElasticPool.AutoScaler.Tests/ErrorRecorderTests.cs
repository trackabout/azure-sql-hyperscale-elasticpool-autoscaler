using Microsoft.Extensions.Logging;
using Moq;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class ErrorRecorderTests
{
    // Sentry's logger integration turns each LogError into one Sentry event, so
    // ErrorRecorder must log exactly once and must not also capture to Sentry itself.
    [Fact]
    public void RecordError_WithException_LogsOneErrorCarryingTheException()
    {
        var logger = new Mock<ILogger<ErrorRecorder>>();
        var recorder = new ErrorRecorder(logger.Object);
        var exception = new FileNotFoundException("missing assembly");

        recorder.RecordError(exception, "Failed to get elastic pool.");

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString() == "Failed to get elastic pool."),
            exception,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public void RecordError_MessageOnly_LogsOneError()
    {
        var logger = new Mock<ILogger<ErrorRecorder>>();
        var recorder = new ErrorRecorder(logger.Object);

        recorder.RecordError("Metrics query returned nothing.");

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString() == "Metrics query returned nothing."),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.VerifyNoOtherCalls();
    }
}
