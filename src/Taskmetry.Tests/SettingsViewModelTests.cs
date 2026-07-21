using Taskmetry.Services;
using Taskmetry.ViewModels;

namespace Taskmetry.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void JSON保存失敗時は自動起動を元の状態へ戻す()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var blockingFile = Path.Combine(directory, "blocked");
            File.WriteAllText(blockingFile, "directory creation blocker");
            var settingsService = new SettingsService(Path.Combine(blockingFile, "settings.json"));
            _ = settingsService.Load();
            var startup = new FakeStartupService(false);
            var viewModel = new SettingsViewModel(settingsService, startup, new FakeDataFolderService());
            viewModel.StartWithWindows = true;

            viewModel.SaveCommand.Execute(null);

            Assert.False(startup.Enabled);
            Assert.Contains("保存できませんでした", viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 位置リセットの保存失敗をUI状態へ通知する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var blockingFile = Path.Combine(directory, "blocked");
            File.WriteAllText(blockingFile, "directory creation blocker");
            var settingsService = new SettingsService(Path.Combine(blockingFile, "settings.json"));
            _ = settingsService.Load();
            var viewModel = new SettingsViewModel(
                settingsService,
                new FakeStartupService(false),
                new FakeDataFolderService());

            viewModel.ResetPositionCommand.Execute(null);

            Assert.Contains("位置をリセットできませんでした", viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ドラッグ位置の保存失敗は再配置後も表示する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var blockingFile = Path.Combine(directory, "blocked");
            File.WriteAllText(blockingFile, "directory creation blocker");
            var settingsService = new SettingsService(Path.Combine(blockingFile, "settings.json"));
            _ = settingsService.Load();
            using var viewModel = new TaskbarViewModel(
                settingsService,
                new SystemMetricsService(),
                new TokenUsageService(directory));

            var saved = viewModel.SaveManualOffset(42);
            viewModel.ApplyPlacement(new TaskbarPlacementService.PlacementResult(
                X: 0,
                Y: 0,
                Width: 500,
                Height: 48,
                UsedBlankGap: true,
                IsOutside: false,
                Edge: TaskbarEdge.Bottom,
                BaseMainPosition: 0));

            Assert.False(saved);
            Assert.Contains("位置を保存できませんでした", viewModel.LayoutHint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 保存先を開く処理をサービス境界へ委譲する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsService = new SettingsService(Path.Combine(directory, "settings.json"));
            _ = settingsService.Load();
            var dataFolder = new FakeDataFolderService();
            var viewModel = new SettingsViewModel(
                settingsService,
                new FakeStartupService(false),
                dataFolder);

            viewModel.OpenDataFolderCommand.Execute(null);

            Assert.True(dataFolder.WasOpened);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Taskmetry.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeStartupService(bool enabled) : IStartupService
    {
        internal bool Enabled { get; private set; } = enabled;

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabledValue) => Enabled = enabledValue;
    }

    private sealed class FakeDataFolderService : IDataFolderService
    {
        internal bool WasOpened { get; private set; }

        public void Open() => WasOpened = true;
    }
}
