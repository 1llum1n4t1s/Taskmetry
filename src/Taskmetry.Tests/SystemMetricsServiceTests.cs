using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class SystemMetricsServiceTests
{
    [Theory]
    [InlineData(40, 70, 30, 60)]
    [InlineData(100, 100, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    public void CPU差分から使用率を算出できる(ulong idle, ulong kernel, ulong user, double expected)
    {
        Assert.Equal(expected, SystemMetricsService.CalculateCpuPercent(idle, kernel, user), precision: 5);
    }
}
