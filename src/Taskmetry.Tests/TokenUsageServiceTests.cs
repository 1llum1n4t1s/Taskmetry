using System.Text.Json;
using Taskmetry.Models;
using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class TokenUsageServiceTests
{
    [Fact]
    public void Codex公式応答からChatGPTアカウントを解析できる()
    {
        using var document = JsonDocument.Parse("""
            {
              "account": {
                "type": "chatgpt",
                "email": "user@example.com",
                "planType": "pro"
              },
              "requiresOpenaiAuth": true
            }
            """);

        var account = CodexAppServerClient.ParseAccount(document.RootElement);

        Assert.True(account.IsAuthenticated);
        Assert.Equal("chatgpt", account.AccountType);
        Assert.Equal("user@example.com", account.Email);
        Assert.Equal("pro", account.PlanType);
    }

    [Fact]
    public void 未認証のCodex公式応答を安全に扱える()
    {
        using var document = JsonDocument.Parse("""{"account":null,"requiresOpenaiAuth":true}""");

        var account = CodexAppServerClient.ParseAccount(document.RootElement);

        Assert.False(account.IsAuthenticated);
    }

    [Fact]
    public void Codex公式応答から複数の利用枠とリセット時刻を解析できる()
    {
        using var document = JsonDocument.Parse("""
            {
              "rateLimits": {
                "limitId": "codex",
                "limitName": null,
                "primary": {
                  "usedPercent": 37.5,
                  "windowDurationMins": 300,
                  "resetsAt": 1784678400
                },
                "secondary": {
                  "usedPercent": 72,
                  "windowDurationMins": 10080,
                  "resetsAt": 1784851200
                }
              }
            }
            """);

        var limits = CodexAppServerClient.ParseRateLimits(document.RootElement);

        Assert.Collection(
            limits.Windows,
            primary =>
            {
                Assert.Equal("5時間枠", primary.Label);
                Assert.Equal(37.5, primary.UsedPercent);
                Assert.Equal(300, primary.WindowDurationMinutes);
                Assert.NotNull(primary.ResetsAt);
            },
            secondary =>
            {
                Assert.Equal("1週間枠", secondary.Label);
                Assert.Equal(72, secondary.UsedPercent);
                Assert.Equal(10_080, secondary.WindowDurationMinutes);
                Assert.NotNull(secondary.ResetsAt);
            });
    }

    [Fact]
    public void Codexデスクトップ版の最新実体を安全に選択する()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Taskmetry.Tests-{Guid.NewGuid():N}");
        try
        {
            var older = Path.Combine(directory, "OpenAI", "Codex", "bin", "older");
            var newer = Path.Combine(directory, "OpenAI", "Codex", "bin", "newer");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            File.WriteAllBytes(Path.Combine(older, "codex.exe"), []);
            File.WriteAllBytes(Path.Combine(newer, "codex.exe"), []);
            Directory.SetLastWriteTimeUtc(older, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(newer, new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));

            var executable = CodexAppServerClient.FindInstalledCodexExecutable(directory);

            Assert.Equal(Path.Combine(newer, "codex.exe"), executable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task 画面更新が短くても公式APIは60秒間再取得しない()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var client = FakeCodexAppServerClient.Connected();
        using var service = new TokenUsageService(client, time);

        var first = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        var second = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.Same(first["Codex"], second["Codex"]);
        Assert.Equal(1, client.AccountReadCount);
        Assert.Equal(1, client.RateLimitsReadCount);

        time.Advance(TimeSpan.FromSeconds(61));
        _ = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.Equal(2, client.AccountReadCount);
        Assert.Equal(2, client.RateLimitsReadCount);
    }

    [Fact]
    public async Task 未認証時は公式API利用枠を読まずWeb認証を案内する()
    {
        var client = new FakeCodexAppServerClient
        {
            Account = new CodexAccountInfo(false),
        };
        using var service = new TokenUsageService(client);

        var snapshots = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        var states = await service.ReadConnectionStatesAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(TokenAvailabilityReason.AuthenticationRequired, snapshots["Codex"].AvailabilityReason);
        Assert.Equal(LlmConnectionStatus.AuthenticationRequired, states["Codex"].Status);
        Assert.Equal(0, client.RateLimitsReadCount);
    }

    [Fact]
    public async Task ClaudeはSessionTokenを要求しGeminiは公式API未提供とする()
    {
        var client = FakeCodexAppServerClient.Connected();
        using var service = new TokenUsageService(client);

        var snapshots = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.Equal(TokenAvailabilityReason.AuthenticationRequired, snapshots["Claude"].AvailabilityReason);
        Assert.Equal(TokenAvailabilityReason.OfficialApiUnavailable, snapshots["Gemini"].AvailabilityReason);
    }

    [Fact]
    public async Task Claude接続後に公式Web使用率へ切り替わる()
    {
        var codexClient = FakeCodexAppServerClient.Connected();
        var claudeClient = new FakeClaudeWebUsageClient
        {
            ConnectResult = new ClaudeWebUsageResult(
                [new TokenUsageWindow("5時間枠", 63, 300)],
                "Personal"),
        };
        using var service = new TokenUsageService(codexClient, claudeClient);

        var connection = await service.ConnectClaudeAsync(
            "sk-ant-sid01-test-session-token",
            TestContext.Current.CancellationToken);
        var snapshots = await service.ReadAllAsync(
            new AppSettings(),
            TestContext.Current.CancellationToken);

        Assert.True(connection.IsConnected);
        Assert.Equal(63, snapshots["Claude"].UsagePercent);
        Assert.Equal("Personal", snapshots["Claude"].AccountLabel);
        Assert.Equal(1, claudeClient.ConnectCount);
        Assert.Equal(0, claudeClient.ReadCount);
    }

    [Fact]
    public async Task 保存済みClaudeTokenが不正でも更新処理を停止しない()
    {
        var claudeClient = new FakeClaudeWebUsageClient
        {
            HasStoredSessionKey = true,
            ReadFailure = new ArgumentException("invalid saved token"),
        };
        using var service = new TokenUsageService(
            FakeCodexAppServerClient.Connected(),
            claudeClient);

        var snapshots = await service.ReadAllAsync(
            new AppSettings(),
            TestContext.Current.CancellationToken);
        var states = await service.ReadConnectionStatesAsync(
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(TokenAvailabilityReason.AuthenticationRequired, snapshots["Claude"].AvailabilityReason);
        Assert.Equal(LlmConnectionStatus.AuthenticationRequired, states["Claude"].Status);
    }

    [Fact]
    public async Task 公式サービスの一時障害は例外を漏らさず再試行状態にする()
    {
        var client = new FakeCodexAppServerClient
        {
            AccountFailure = new IOException("temporary failure"),
        };
        using var service = new TokenUsageService(client);

        var snapshots = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        var states = await service.ReadConnectionStatesAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(TokenAvailabilityReason.NetworkError, snapshots["Codex"].AvailabilityReason);
        Assert.Equal(LlmConnectionStatus.NetworkError, states["Codex"].Status);
    }

    [Fact]
    public async Task Web認証完了後にCodex公式使用率へ切り替わる()
    {
        var client = new FakeCodexAppServerClient
        {
            Account = new CodexAccountInfo(false),
            LoginCompletesSuccessfully = true,
        };
        using var service = new TokenUsageService(client);

        var login = await service.BeginCodexLoginAsync(TestContext.Current.CancellationToken);
        client.Account = new CodexAccountInfo(true, "chatgpt", "user@example.com", "plus");
        client.RateLimits = new CodexRateLimits([new TokenUsageWindow("5時間枠", 48, 300)]);
        var connection = await service.CompleteCodexLoginAsync(
            login.LoginId,
            TestContext.Current.CancellationToken);

        Assert.True(connection.IsConnected);
        var snapshots = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        Assert.Equal(48, snapshots["Codex"].UsagePercent);
    }

    [Fact]
    public async Task 公式APIの不正応答でも例外を漏らさず再試行状態にする()
    {
        var client = new FakeCodexAppServerClient
        {
            AccountFailure = new InvalidDataException("invalid account payload"),
        };
        using var service = new TokenUsageService(client);

        var snapshots = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        var states = await service.ReadConnectionStatesAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(TokenAvailabilityReason.NetworkError, snapshots["Codex"].AvailabilityReason);
        Assert.Equal(LlmConnectionStatus.NetworkError, states["Codex"].Status);
    }

    [Fact]
    public async Task 非表示のプロバイダーは公式APIを取得しない()
    {
        var codexClient = FakeCodexAppServerClient.Connected();
        var claudeClient = new FakeClaudeWebUsageClient { HasStoredSessionKey = true };
        using var service = new TokenUsageService(codexClient, claudeClient);

        _ = await service.ReadAllAsync(
            new AppSettings { ShowCodex = false, ShowClaude = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, codexClient.AccountReadCount);
        Assert.Equal(0, claudeClient.ReadCount);
    }

    [Fact]
    public async Task 失効したClaudeTokenは再接続まで再送しない()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var claudeClient = new FakeClaudeWebUsageClient
        {
            HasStoredSessionKey = true,
            ReadFailure = new ClaudeWebUsageException(
                ClaudeWebFailureKind.AuthenticationRequired,
                "expired session token"),
        };
        using var service = new TokenUsageService(
            FakeCodexAppServerClient.Connected(),
            claudeClient,
            time);

        _ = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(6));
        var snapshots = await service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.Equal(1, claudeClient.ReadCount);
        Assert.Equal(TokenAvailabilityReason.AuthenticationRequired, snapshots["Claude"].AvailabilityReason);
    }

    [Fact]
    public async Task Codex切断は進行中の更新に上書きされない()
    {
        var client = FakeCodexAppServerClient.Connected();
        var accountReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AccountReadGate = accountReadGate;
        using var service = new TokenUsageService(client);

        var refresh = service.ReadAllAsync(new AppSettings(), TestContext.Current.CancellationToken);
        while (client.AccountReadCount == 0)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        var disconnect = service.DisconnectCodexAsync(TestContext.Current.CancellationToken);
        Assert.False(disconnect.IsCompleted);

        accountReadGate.SetResult();
        _ = await refresh;
        var disconnected = await disconnect;
        var states = await service.ReadConnectionStatesAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(LlmConnectionStatus.AuthenticationRequired, disconnected.Status);
        Assert.Equal(LlmConnectionStatus.AuthenticationRequired, states["Codex"].Status);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeCodexAppServerClient : ICodexAppServerClient
    {
        internal CodexAccountInfo Account { get; set; } = new(false);
        internal CodexRateLimits RateLimits { get; set; } = new([]);
        internal bool LoginCompletesSuccessfully { get; set; }
        internal Exception? AccountFailure { get; set; }
        internal TaskCompletionSource? AccountReadGate { get; set; }
        internal int AccountReadCount { get; private set; }
        internal int RateLimitsReadCount { get; private set; }

        internal static FakeCodexAppServerClient Connected() => new()
        {
            Account = new CodexAccountInfo(true, "chatgpt", "user@example.com", "pro"),
            RateLimits = new CodexRateLimits([new TokenUsageWindow("5時間枠", 42, 300)]),
        };

        public async Task<CodexAccountInfo> ReadAccountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccountReadCount++;
            if (AccountReadGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            if (AccountFailure is { } failure)
            {
                throw failure;
            }

            return Account;
        }

        public Task<CodexRateLimits> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RateLimitsReadCount++;
            return Task.FromResult(RateLimits);
        }

        public Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CodexLoginStart(
                "login-1",
                new Uri("https://chatgpt.com/auth")));
        }

        public Task<bool> WaitForLoginCompletionAsync(string loginId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("login-1", loginId);
            return Task.FromResult(LoginCompletesSuccessfully);
        }

        public Task LogoutAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Account = new CodexAccountInfo(false);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeClaudeWebUsageClient : IClaudeWebUsageClient
    {
        internal ClaudeWebUsageResult ConnectResult { get; set; } = new([]);
        internal Exception? ReadFailure { get; set; }
        internal int ConnectCount { get; private set; }
        internal int ReadCount { get; private set; }
        private bool _hasSessionKey;

        internal bool HasStoredSessionKey
        {
            set => _hasSessionKey = value;
        }

        public bool HasSessionKey() => _hasSessionKey;

        public Task<ClaudeWebUsageResult> ReadUsageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (ReadFailure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(ConnectResult);
        }

        public Task<ClaudeWebUsageResult> ConnectAsync(
            string sessionKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            _hasSessionKey = true;
            return Task.FromResult(ConnectResult);
        }

        public void Disconnect() => _hasSessionKey = false;

        public void Dispose()
        {
        }
    }
}
