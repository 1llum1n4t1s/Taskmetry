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
            ClaudeContextLimit = 10,
            GeminiContextLimit = 8_000_000,
            PlacementMode = (RailPlacementMode)99,
            ManualOffsetPixels = 99_999,
        };

        settings.Sanitize();

        Assert.Equal(1_200, settings.PreferredWidthPixels);
        Assert.Equal(1, settings.RefreshIntervalSeconds);
        Assert.Equal(32_000, settings.ClaudeContextLimit);
        Assert.Equal(4_000_000, settings.GeminiContextLimit);
        Assert.Equal(RailPlacementMode.Auto, settings.PlacementMode);
        Assert.Equal(10_000, settings.ManualOffsetPixels);
    }
}
