using Microsoft.Extensions.Logging;
using Moq;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class RunHeartbeatTests
{
    [Fact]
    public void RunCompleted_LogsOneInformationLineNamingTheServer()
    {
        var logger = new Mock<ILogger<RunHeartbeat>>();
        var heartbeat = new RunHeartbeat(logger.Object);

        heartbeat.RunCompleted("example-sql-server");

        logger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString() == "AutoScaler run completed for server example-sql-server"),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
