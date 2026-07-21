using Taskmetry.Services;
using Taskmetry.Models;

namespace Taskmetry.Tests;

public sealed class TaskbarPlacementServiceTests
{
    [Fact]
    public void 中央タスク群と通知領域の空きへ右寄せ配置する()
    {
        var taskbar = new TaskbarPlacementService.Rectangle(0, 1380, 2560, 1440);
        var tray = new TaskbarPlacementService.Rectangle(2300, 1380, 2560, 1440);
        var rebar = new TaskbarPlacementService.Rectangle(895, 1380, 1500, 1440);
        var monitor = new TaskbarPlacementService.Rectangle(0, 0, 2560, 1440);

        var result = TaskbarPlacementService.CalculatePlacement(taskbar, tray, rebar, monitor, 740);

        Assert.True(result.UsedBlankGap);
        Assert.Equal(1554, result.X);
        Assert.Equal(1384, result.Y);
        Assert.Equal(740, result.Width);
        Assert.Equal(52, result.Height);
    }

    [Fact]
    public void 希望幅が空き領域より大きい場合はアイコンを覆わない()
    {
        var taskbar = new TaskbarPlacementService.Rectangle(0, 1040, 1920, 1080);
        var tray = new TaskbarPlacementService.Rectangle(1700, 1040, 1920, 1080);
        var rebar = new TaskbarPlacementService.Rectangle(600, 1040, 1300, 1080);
        var monitor = new TaskbarPlacementService.Rectangle(0, 0, 1920, 1080);

        var result = TaskbarPlacementService.CalculatePlacement(taskbar, tray, rebar, monitor, 1200);

        Assert.True(result.UsedBlankGap);
        Assert.Equal(388, result.Width);
        Assert.Equal(1306, result.X);
        Assert.Equal(1694, result.X + result.Width);
    }

    [Fact]
    public void ピン留めが多く空き不足ならタスクバー外側へ退避する()
    {
        var taskbar = new TaskbarPlacementService.Rectangle(0, 1040, 1920, 1080);
        var tray = new TaskbarPlacementService.Rectangle(1700, 1040, 1920, 1080);
        var rebar = new TaskbarPlacementService.Rectangle(600, 1040, 1500, 1080);
        var monitor = new TaskbarPlacementService.Rectangle(0, 0, 1920, 1080);

        var result = TaskbarPlacementService.CalculatePlacement(taskbar, tray, rebar, monitor, 740);

        Assert.False(result.UsedBlankGap);
        Assert.True(result.IsOutside);
        Assert.Equal(TaskbarEdge.Bottom, result.Edge);
        Assert.True(result.Y + result.Height < taskbar.Top);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Explorer内部領域が不明ならアイコン保護のため外側へ退避する(bool hasTray, bool hasRebar)
    {
        var taskbar = new TaskbarPlacementService.Rectangle(0, 1040, 1920, 1080);
        var tray = hasTray
            ? new TaskbarPlacementService.Rectangle(1700, 1040, 1920, 1080)
            : default;
        var rebar = hasRebar
            ? new TaskbarPlacementService.Rectangle(600, 1040, 1300, 1080)
            : default;
        var monitor = new TaskbarPlacementService.Rectangle(0, 0, 1920, 1080);

        var result = TaskbarPlacementService.CalculatePlacement(taskbar, tray, rebar, monitor, 740);

        Assert.False(result.UsedBlankGap);
        Assert.True(result.IsOutside);
        Assert.True(result.Y + result.Height < taskbar.Top);
    }

    [Theory]
    [InlineData(0, 0, 1920, 48, TaskbarEdge.Top)]
    [InlineData(0, 1032, 1920, 1080, TaskbarEdge.Bottom)]
    [InlineData(0, 0, 48, 1080, TaskbarEdge.Left)]
    [InlineData(1872, 0, 1920, 1080, TaskbarEdge.Right)]
    public void 上下左右のタスクバー辺を判定する(int left, int top, int right, int bottom, TaskbarEdge expected)
    {
        var taskbar = new TaskbarPlacementService.Rectangle(left, top, right, bottom);
        var monitor = new TaskbarPlacementService.Rectangle(0, 0, 1920, 1080);

        Assert.Equal(expected, TaskbarPlacementService.DetectEdge(taskbar, monitor));
    }

    [Fact]
    public void 縦タスクバーでは縦長レールを空きへ配置する()
    {
        var taskbar = new TaskbarPlacementService.Rectangle(0, 0, 60, 1440);
        var rebar = new TaskbarPlacementService.Rectangle(0, 180, 60, 700);
        var tray = new TaskbarPlacementService.Rectangle(0, 1200, 60, 1440);
        var monitor = new TaskbarPlacementService.Rectangle(0, 0, 2560, 1440);

        var result = TaskbarPlacementService.CalculatePlacement(
            taskbar, tray, rebar, monitor, 420, RailPlacementMode.Auto, -80);

        Assert.True(result.IsVertical);
        Assert.True(result.UsedBlankGap);
        Assert.Equal(TaskbarEdge.Left, result.Edge);
        Assert.Equal(52, result.Width);
        Assert.Equal(420, result.Height);
        Assert.True(result.Y >= rebar.Bottom + 6);
        Assert.True(result.Y + result.Height <= tray.Top - 6);
    }
}
