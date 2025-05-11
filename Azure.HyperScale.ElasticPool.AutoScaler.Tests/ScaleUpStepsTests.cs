using Microsoft.Extensions.Logging;
using Moq;

namespace Azure.HyperScale.ElasticPool.AutoScaler.Tests;

public class ScaleUpStepsTests
{
    private readonly Mock<ILogger<AutoScaler>> _loggerMock;
    private readonly Mock<ISqlRepository> _sqlRepositoryMock;
    private readonly Mock<IErrorRecorder> _errorRecorderMock;
    private readonly Mock<IAzureResourceService> _azureResourceServiceMock;

    public ScaleUpStepsTests()
    {
        _loggerMock = new Mock<ILogger<AutoScaler>>();
        _sqlRepositoryMock = new Mock<ISqlRepository>();
        _errorRecorderMock = new Mock<IErrorRecorder>();
        _azureResourceServiceMock = new Mock<IAzureResourceService>();
    }

    [Theory]
    [InlineData(1, 4, 6)]   // With steps=1, should go from 4 to 6
    [InlineData(2, 4, 8)]   // With steps=2, should go from 4 to 8
    [InlineData(3, 4, 10)]  // With steps=3, should go from 4 to 10
    [InlineData(4, 4, 10)]  // With steps=4, should go from 4 to 10 (limited by options)
    public void ScaleUp_Should_Skip_Steps_According_To_ScaleUpSteps(int scaleUpSteps, double currentVCore, double expectedVCore)
    {
        // Arrange
        var config = new Mock<IAutoScalerConfiguration>();
        config.Setup(c => c.VCoreOptions).Returns([4, 6, 8, 10]);
        config.Setup(c => c.VCoreCeiling).Returns(10.0);
        config.Setup(c => c.VCoreFloor).Returns(4.0);
        config.Setup(c => c.ScaleUpSteps).Returns(scaleUpSteps);
        config.Setup(c => c.GetVCoreFloorForPool(It.IsAny<string>())).Returns(4.0);
        config.Setup(c => c.GetPerDatabaseMaxByVCore(It.IsAny<double>())).Returns(2.0);

        var autoScaler = new AutoScaler(
            _loggerMock.Object,
            config.Object,
            _sqlRepositoryMock.Object,
            _errorRecorderMock.Object,
            _azureResourceServiceMock.Object);

        var usageInfo = new UsageInfo
        {
            ElasticPoolName = "testpool",
            ElasticPoolCpuLimit = (int)currentVCore,
            ShortAvgCpu = 90,
            LongAvgCpu = 90,
            ShortWorkersPercent = 50,
            LongWorkersPercent = 50,
            ShortInstanceCpu = 50,
            LongInstanceCpu = 50,
            ShortDataIo = 50,
            LongDataIo = 50
        };

        // Act
        var result = autoScaler.GetNewPoolTarget(usageInfo, currentVCore);

        // Assert
        Assert.Equal(expectedVCore, result.VCore);
    }

    [Theory]
    [InlineData(1, 8, 6)]   // Always scale down by 1 step regardless of ScaleUpSteps
    [InlineData(2, 8, 6)]   // Always scale down by 1 step regardless of ScaleUpSteps
    [InlineData(3, 8, 6)]   // Always scale down by 1 step regardless of ScaleUpSteps
    public void ScaleDown_Should_Always_Scale_One_Step_Regardless_Of_ScaleUpSteps(int scaleUpSteps, double currentVCore, double expectedVCore)
    {
        // Arrange
        var config = new Mock<IAutoScalerConfiguration>();
        config.Setup(c => c.VCoreOptions).Returns([4, 6, 8, 10]);
        config.Setup(c => c.VCoreCeiling).Returns(10.0);
        config.Setup(c => c.VCoreFloor).Returns(4.0);
        config.Setup(c => c.ScaleUpSteps).Returns(scaleUpSteps);
        config.Setup(c => c.GetVCoreFloorForPool(It.IsAny<string>())).Returns(4.0);
        config.Setup(c => c.GetPerDatabaseMaxByVCore(It.IsAny<double>())).Returns(2.0);

        // Set up all low and high CPU metrics for scale down
        config.Setup(c => c.LowCpuPercent).Returns(30m);
        config.Setup(c => c.LowWorkersPercent).Returns(30m);
        config.Setup(c => c.LowInstanceCpuPercent).Returns(30m);
        config.Setup(c => c.LowDataIoPercent).Returns(30m);

        // Set up high CPU thresholds (must be higher than the test values of 20)
        config.Setup(c => c.HighCpuPercent).Returns(70m);
        config.Setup(c => c.HighWorkersPercent).Returns(70m);
        config.Setup(c => c.HighInstanceCpuPercent).Returns(70m);
        config.Setup(c => c.HighDataIoPercent).Returns(70m);

        var autoScaler = new AutoScaler(
            _loggerMock.Object,
            config.Object,
            _sqlRepositoryMock.Object,
            _errorRecorderMock.Object,
            _azureResourceServiceMock.Object);

        var usageInfo = new UsageInfo
        {
            ElasticPoolName = "testpool",
            ElasticPoolCpuLimit = (int)currentVCore,
            ShortAvgCpu = 20,
            LongAvgCpu = 20,
            ShortWorkersPercent = 20,
            LongWorkersPercent = 20,
            ShortInstanceCpu = 20,
            LongInstanceCpu = 20,
            ShortDataIo = 20,
            LongDataIo = 20
        };

        // Act
        var result = autoScaler.GetNewPoolTarget(usageInfo, currentVCore);

        // Assert
        Assert.Equal(expectedVCore, result.VCore);
    }

    [Fact]
    public void ScaleUp_Should_Not_Exceed_VCoreCeiling()
    {
        // Arrange
        var config = new Mock<IAutoScalerConfiguration>();
        config.Setup(c => c.VCoreOptions).Returns([4, 6, 8, 10, 12, 16]);
        config.Setup(c => c.VCoreCeiling).Returns(10.0);
        config.Setup(c => c.VCoreFloor).Returns(4.0);
        config.Setup(c => c.ScaleUpSteps).Returns(3); // This would try to jump 3 steps
        config.Setup(c => c.GetVCoreFloorForPool(It.IsAny<string>())).Returns(4.0);
        config.Setup(c => c.GetPerDatabaseMaxByVCore(It.IsAny<double>())).Returns(2.0);

        var autoScaler = new AutoScaler(
            _loggerMock.Object,
            config.Object,
            _sqlRepositoryMock.Object,
            _errorRecorderMock.Object,
            _azureResourceServiceMock.Object);

        var usageInfo = new UsageInfo
        {
            ElasticPoolName = "testpool",
            ElasticPoolCpuLimit = 8, // Current at 8, with ScaleUpSteps=3 would try to go to 16, but ceiling is 10
            ShortAvgCpu = 90,
            LongAvgCpu = 90,
            ShortWorkersPercent = 90,
            LongWorkersPercent = 90,
            ShortInstanceCpu = 50,
            LongInstanceCpu = 50,
            ShortDataIo = 50,
            LongDataIo = 50
        };

        // Act
        var result = autoScaler.GetNewPoolTarget(usageInfo, 8);

        // Assert
        Assert.Equal(10.0, result.VCore); // Should be limited to ceiling of 10
    }
}