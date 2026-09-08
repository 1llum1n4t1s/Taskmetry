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
    public void 左右分割とメーターの振り分けを保存して読み直す()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var settingsService = new SettingsService(path);
            _ = settingsService.Load();
            var viewModel = new SettingsViewModel(
                settingsService,
                new FakeStartupService(false),
                new FakeDataFolderService());

            viewModel.SplitRail = true;
            viewModel.CpuSide = (int)Taskmetry.Models.RailSide.Right;
            viewModel.ClaudeSide = (int)Taskmetry.Models.RailSide.Left;
            viewModel.SaveCommand.Execute(null);

            var reloaded = new SettingsService(path);
            var settings = reloaded.Load().Settings;

            Assert.True(settings.SplitRail);
            Assert.Equal(Taskmetry.Models.RailSide.Right, settings.CpuSide);
            Assert.Equal(Taskmetry.Models.RailSide.Left, settings.ClaudeSide);
            Assert.Equal(Taskmetry.Models.RailSide.Right, settings.CodexSide);
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
                new StubLlmUsageService());

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

    [Fact]
    public async Task CodexのWeb認証URLを既定ブラウザーへ渡して接続状態を更新する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsService = new SettingsService(Path.Combine(directory, "settings.json"));
            _ = settingsService.Load();
            var llmUsage = new FakeLlmUsageService();
            var browser = new FakeExternalBrowserService();
            var viewModel = new SettingsViewModel(
                settingsService,
                new FakeStartupService(false),
                new FakeDataFolderService(),
                llmUsage,
                browser);

            await viewModel.RefreshConnectionsCommand.ExecuteAsync(null);
            await viewModel.ToggleCodexConnectionCommand.ExecuteAsync(null);

            Assert.Equal(new Uri("https://chatgpt.com/auth"), browser.OpenedUri);
            Assert.Equal("接続を解除", viewModel.CodexActionText);
            Assert.Contains("u***@example.com", viewModel.CodexConnectionText);
            Assert.True(viewModel.IsCodexActionEnabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeのSessionTokenを接続後に入力欄から消去する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsService = new SettingsService(Path.Combine(directory, "settings.json"));
            _ = settingsService.Load();
            var llmUsage = new FakeLlmUsageService();
            var viewModel = new SettingsViewModel(
                settingsService,
                new FakeStartupService(false),
                new FakeDataFolderService(),
                llmUsage,
                new FakeExternalBrowserService());

            await viewModel.RefreshConnectionsCommand.ExecuteAsync(null);
            viewModel.ClaudeSessionKey = "sk-ant-sid01-test-session-token";
            await viewModel.ToggleClaudeConnectionCommand.ExecuteAsync(null);

            Assert.Equal("sk-ant-sid01-test-session-token", llmUsage.ReceivedClaudeSessionKey);
            Assert.Equal(string.Empty, viewModel.ClaudeSessionKey);
            Assert.Equal("接続を解除", viewModel.ClaudeActionText);
            Assert.False(viewModel.IsClaudeSessionInputVisible);
            Assert.True(viewModel.IsClaudeActionEnabled);
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

    private sealed class StubLlmUsageService : ILlmUsageService
    {
        public Task<IReadOnlyDictionary<string, Taskmetry.Models.TokenUsageSnapshot>> ReadAllAsync(
            Taskmetry.Models.AppSettings settings,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, Taskmetry.Models.TokenUsageSnapshot>>(
                new Dictionary<string, Taskmetry.Models.TokenUsageSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Codex"] = Taskmetry.Models.TokenUsageSnapshot.Unavailable(
                        "Codex",
                        Taskmetry.Models.TokenAvailabilityReason.AuthenticationRequired),
                    ["Claude"] = Taskmetry.Models.TokenUsageSnapshot.Unavailable(
                        "Claude",
                        Taskmetry.Models.TokenAvailabilityReason.OfficialApiUnavailable),
                    ["Gemini"] = Taskmetry.Models.TokenUsageSnapshot.Unavailable(
                        "Gemini",
                        Taskmetry.Models.TokenAvailabilityReason.OfficialApiUnavailable),
                });

        public Task<IReadOnlyDictionary<string, Taskmetry.Models.LlmConnectionSnapshot>> ReadConnectionStatesAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LlmLoginStart> BeginCodexLoginAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Taskmetry.Models.LlmConnectionSnapshot> CompleteCodexLoginAsync(
            string loginId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Taskmetry.Models.LlmConnectionSnapshot> DisconnectCodexAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Taskmetry.Models.LlmConnectionSnapshot> ConnectClaudeAsync(
            string sessionKey,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Taskmetry.Models.LlmConnectionSnapshot> DisconnectClaudeAsync(
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FakeLlmUsageService : ILlmUsageService
    {
        private Taskmetry.Models.LlmConnectionSnapshot _codex = new(
            "Codex",
            Taskmetry.Models.LlmConnectionStatus.AuthenticationRequired,
            "Web認証が必要です");
        private Taskmetry.Models.LlmConnectionSnapshot _claude = new(
            "Claude",
            Taskmetry.Models.LlmConnectionStatus.AuthenticationRequired,
            "Session Tokenが必要です");

        internal string? ReceivedClaudeSessionKey { get; private set; }

        public Task<IReadOnlyDictionary<string, Taskmetry.Models.TokenUsageSnapshot>> ReadAllAsync(
            Taskmetry.Models.AppSettings settings,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, Taskmetry.Models.LlmConnectionSnapshot>> ReadConnectionStatesAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, Taskmetry.Models.LlmConnectionSnapshot>>(
                new Dictionary<string, Taskmetry.Models.LlmConnectionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Codex"] = _codex,
                    ["Claude"] = _claude,
                    ["Gemini"] = new(
                        "Gemini",
                        Taskmetry.Models.LlmConnectionStatus.OfficialApiUnavailable,
                        "公式API未提供"),
                });

        public Task<LlmLoginStart> BeginCodexLoginAsync(CancellationToken cancellationToken)
            => Task.FromResult(new LlmLoginStart(
                "login-1",
                new Uri("https://chatgpt.com/auth")));

        public Task<Taskmetry.Models.LlmConnectionSnapshot> CompleteCodexLoginAsync(
            string loginId,
            CancellationToken cancellationToken)
        {
            _codex = new Taskmetry.Models.LlmConnectionSnapshot(
                "Codex",
                Taskmetry.Models.LlmConnectionStatus.Connected,
                "OpenAI公式APIへ接続済み",
                "user@example.com");
            return Task.FromResult(_codex);
        }

        public Task<Taskmetry.Models.LlmConnectionSnapshot> DisconnectCodexAsync(CancellationToken cancellationToken)
        {
            _codex = new Taskmetry.Models.LlmConnectionSnapshot(
                "Codex",
                Taskmetry.Models.LlmConnectionStatus.AuthenticationRequired,
                "接続を解除しました");
            return Task.FromResult(_codex);
        }

        public Task<Taskmetry.Models.LlmConnectionSnapshot> ConnectClaudeAsync(
            string sessionKey,
            CancellationToken cancellationToken)
        {
            ReceivedClaudeSessionKey = sessionKey;
            _claude = new Taskmetry.Models.LlmConnectionSnapshot(
                "Claude",
                Taskmetry.Models.LlmConnectionStatus.Connected,
                "claude.ai公式Web応答へ接続済み",
                "Personal");
            return Task.FromResult(_claude);
        }

        public Task<Taskmetry.Models.LlmConnectionSnapshot> DisconnectClaudeAsync(
            CancellationToken cancellationToken)
        {
            _claude = new Taskmetry.Models.LlmConnectionSnapshot(
                "Claude",
                Taskmetry.Models.LlmConnectionStatus.AuthenticationRequired,
                "接続を解除しました");
            return Task.FromResult(_claude);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeExternalBrowserService : IExternalBrowserService
    {
        internal Uri? OpenedUri { get; private set; }

        public void Open(Uri uri) => OpenedUri = uri;
    }
}
