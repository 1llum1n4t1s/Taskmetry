using Taskmetry.Models;

namespace Taskmetry.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void 設定値を安全な範囲へ丸める()
    {
        var settings = new AppSettings
        {
            PreferredWidthPixels = 9_999,
            RefreshIntervalSeconds = 0,
            PlacementMode = (RailPlacementMode)99,
            ManualOffsetPixels = 99_999,
        };

        settings.Sanitize();

        Assert.Equal(1_200, settings.PreferredWidthPixels);
        Assert.Equal(1, settings.RefreshIntervalSeconds);
        Assert.Equal(RailPlacementMode.Auto, settings.PlacementMode);
        Assert.Equal(10_000, settings.ManualOffsetPixels);
    }

    [Fact]
    public void 不正なレール振り分けは左へ丸める()
    {
        var settings = new AppSettings
        {
            CpuSide = (RailSide)42,
            ClaudeSide = RailSide.Right,
        };

        settings.Sanitize();

        Assert.Equal(RailSide.Left, settings.CpuSide);
        Assert.Equal(RailSide.Right, settings.ClaudeSide);
    }

    [Fact]
    public void 既定では左にシステム指標と右にAI使用率を振り分ける()
    {
        var settings = new AppSettings();

        Assert.False(settings.SplitRail);
        Assert.Equal(RailSide.Left, settings.CpuSide);
        Assert.Equal(RailSide.Left, settings.MemorySide);
        Assert.Equal(RailSide.Right, settings.CodexSide);
        Assert.Equal(RailSide.Right, settings.ClaudeSide);
        Assert.Equal(RailSide.Right, settings.GeminiSide);
    }
}
