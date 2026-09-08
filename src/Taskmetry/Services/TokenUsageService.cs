using System.ComponentModel;
using System.Text.Json;
using Taskmetry.Models;

namespace Taskmetry.Services;

public sealed record LlmLoginStart(string LoginId, Uri AuthenticationUri);

public interface ILlmUsageService : IDisposable
{
    Task<IReadOnlyDictionary<string, TokenUsageSnapshot>> ReadAllAsync(
        AppSettings settings,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, LlmConnectionSnapshot>> ReadConnectionStatesAsync(
        bool forceRefresh,
        CancellationToken cancellationToken);

    Task<LlmLoginStart> BeginCodexLoginAsync(CancellationToken cancellationToken);

    Task<LlmConnectionSnapshot> CompleteCodexLoginAsync(
        string loginId,
        CancellationToken cancellationToken);

    Task<LlmConnectionSnapshot> DisconnectCodexAsync(CancellationToken cancellationToken);

    Task<LlmConnectionSnapshot> ConnectClaudeAsync(
        string sessionKey,
        CancellationToken cancellationToken);

    Task<LlmConnectionSnapshot> DisconnectClaudeAsync(CancellationToken cancellationToken);
}

public sealed class TokenUsageService : ILlmUsageService
{
    private static readonly TimeSpan SuccessfulRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailedRefreshInterval = TimeSpan.FromSeconds(15);
    private readonly ICodexAppServerClient _codexClient;
    private readonly IClaudeWebUsageClient _claudeClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _codexRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _claudeRefreshGate = new(1, 1);
    private TokenUsageSnapshot _codexSnapshot = TokenUsageSnapshot.Unavailable(
        "Codex",
        TokenAvailabilityReason.AuthenticationRequired);
    private LlmConnectionSnapshot _codexConnection = AuthenticationRequired();
    private TokenUsageSnapshot _claudeSnapshot = TokenUsageSnapshot.Unavailable(
        "Claude",
        TokenAvailabilityReason.AuthenticationRequired);
    private LlmConnectionSnapshot _claudeConnection = ClaudeAuthenticationRequired();
    private DateTimeOffset _nextCodexRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextClaudeRefreshUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public TokenUsageService()
        : this(new CodexAppServerClient(), new ClaudeWebUsageClient(), TimeProvider.System)
    {
    }

    internal TokenUsageService(ICodexAppServerClient codexClient, TimeProvider? timeProvider = null)
        : this(codexClient, new UnconfiguredClaudeWebUsageClient(), timeProvider)
    {
    }

    internal TokenUsageService(
        ICodexAppServerClient codexClient,
        IClaudeWebUsageClient claudeClient,
        TimeProvider? timeProvider = null)
    {
        _codexClient = codexClient;
        _claudeClient = claudeClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyDictionary<string, TokenUsageSnapshot>> ReadAllAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 非表示のプロバイダーは外部プロセス起動・HTTP送信ごと止め、最後のスナップショットを返す
        var refreshes = new List<Task>(2);
        if (settings.ShowCodex)
        {
            refreshes.Add(RefreshCodexAsync(forceRefresh: false, cancellationToken));
        }

        if (settings.ShowClaude)
        {
            refreshes.Add(RefreshClaudeAsync(forceRefresh: false, cancellationToken));
        }

        await Task.WhenAll(refreshes).ConfigureAwait(false);
        return new Dictionary<string, TokenUsageSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["Codex"] = _codexSnapshot,
            ["Claude"] = _claudeSnapshot,
            ["Gemini"] = TokenUsageSnapshot.Unavailable(
                "Gemini",
                TokenAvailabilityReason.OfficialApiUnavailable),
        };
    }

    public async Task<IReadOnlyDictionary<string, LlmConnectionSnapshot>> ReadConnectionStatesAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.WhenAll(
            RefreshCodexAsync(forceRefresh, cancellationToken),
            RefreshClaudeAsync(forceRefresh, cancellationToken)).ConfigureAwait(false);
        return new Dictionary<string, LlmConnectionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["Codex"] = _codexConnection,
            ["Claude"] = _claudeConnection,
            ["Gemini"] = new(
                "Gemini",
                LlmConnectionStatus.OfficialApiUnavailable,
                "個人アカウントの使用率を定期取得できる公式Web APIは未提供です"),
        };
    }

    public async Task<LlmLoginStart> BeginCodexLoginAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 進行中の定期更新に状態を上書きされないよう、更新と同じゲート内で遷移させる
        await _codexRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var login = await _codexClient.StartChatGptLoginAsync(cancellationToken).ConfigureAwait(false);
            _codexConnection = new LlmConnectionSnapshot(
                "Codex",
                LlmConnectionStatus.AuthenticationInProgress,
                "ブラウザーでOpenAI認証を完了してください");
            _codexSnapshot = TokenUsageSnapshot.Unavailable(
                "Codex",
                TokenAvailabilityReason.AuthenticationInProgress);
            _nextCodexRefreshUtc = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(5);
            return new LlmLoginStart(login.LoginId, login.AuthenticationUri);
        }
        finally
        {
            _codexRefreshGate.Release();
        }
    }

    public async Task<LlmConnectionSnapshot> CompleteCodexLoginAsync(
        string loginId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 認証待ちは長時間になるためゲートの外で待ち、状態書き込みだけをゲート内で行う
        var completed = await _codexClient
            .WaitForLoginCompletionAsync(loginId, cancellationToken)
            .ConfigureAwait(false);

        if (!completed)
        {
            await _codexRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _codexConnection = AuthenticationRequired("OpenAI認証が完了しませんでした");
                _codexSnapshot = TokenUsageSnapshot.Unavailable(
                    "Codex",
                    TokenAvailabilityReason.AuthenticationRequired);
                _nextCodexRefreshUtc = DateTimeOffset.MinValue;
                return _codexConnection;
            }
            finally
            {
                _codexRefreshGate.Release();
            }
        }

        await RefreshCodexAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        return _codexConnection;
    }

    public async Task<LlmConnectionSnapshot> ConnectClaudeAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClaudeWebUsageClient.ValidateSessionKey(sessionKey);
        await _claudeRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            try
            {
                var usage = await _claudeClient
                    .ConnectAsync(sessionKey, cancellationToken)
                    .ConfigureAwait(false);
                ApplyClaudeUsage(usage, now);
                _nextClaudeRefreshUtc = now + SuccessfulRefreshInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ClaudeWebUsageException ex)
            {
                ApplyClaudeFailure(ex, now);
            }
            catch (Exception ex) when (IsServiceFailure(ex))
            {
                ApplyClaudeStorageFailure(now);
            }

            return _claudeConnection;
        }
        finally
        {
            _claudeRefreshGate.Release();
        }
    }

    public async Task<LlmConnectionSnapshot> DisconnectClaudeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _claudeRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _claudeClient.Disconnect();
            _claudeConnection = ClaudeAuthenticationRequired("Claudeとの接続を解除しました");
            _claudeSnapshot = TokenUsageSnapshot.Unavailable(
                "Claude",
                TokenAvailabilityReason.AuthenticationRequired);
            _nextClaudeRefreshUtc = DateTimeOffset.MinValue;
            return _claudeConnection;
        }
        finally
        {
            _claudeRefreshGate.Release();
        }
    }

    public async Task<LlmConnectionSnapshot> DisconnectCodexAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 進行中の定期更新が完了してからログアウトし、切断状態を書き戻されないようにする
        await _codexRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _codexClient.LogoutAsync(cancellationToken).ConfigureAwait(false);
            _codexConnection = AuthenticationRequired("OpenAIアカウントとの接続を解除しました");
            _codexSnapshot = TokenUsageSnapshot.Unavailable(
                "Codex",
                TokenAvailabilityReason.AuthenticationRequired);
            _nextCodexRefreshUtc = DateTimeOffset.MinValue;
            return _codexConnection;
        }
        finally
        {
            _codexRefreshGate.Release();
        }
    }

    private async Task RefreshCodexAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh && now < _nextCodexRefreshUtc)
        {
            return;
        }

        await _codexRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (!forceRefresh && now < _nextCodexRefreshUtc)
            {
                return;
            }

            try
            {
                var account = await _codexClient.ReadAccountAsync(cancellationToken).ConfigureAwait(false);
                if (!account.IsAuthenticated)
                {
                    _codexConnection = AuthenticationRequired();
                    _codexSnapshot = TokenUsageSnapshot.Unavailable(
                        "Codex",
                        TokenAvailabilityReason.AuthenticationRequired);
                    _nextCodexRefreshUtc = now + SuccessfulRefreshInterval;
                    return;
                }

                if (!string.Equals(account.AccountType, "chatgpt", StringComparison.OrdinalIgnoreCase))
                {
                    _codexConnection = new LlmConnectionSnapshot(
                        "Codex",
                        LlmConnectionStatus.UnsupportedAccount,
                        "使用率表示にはChatGPTのWeb認証が必要です",
                        account.Email);
                    _codexSnapshot = TokenUsageSnapshot.Unavailable(
                        "Codex",
                        TokenAvailabilityReason.UnsupportedAccount);
                    _nextCodexRefreshUtc = now + SuccessfulRefreshInterval;
                    return;
                }

                var rateLimits = await _codexClient.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                _codexConnection = new LlmConnectionSnapshot(
                    "Codex",
                    LlmConnectionStatus.Connected,
                    account.PlanType is { Length: > 0 }
                        ? $"OpenAI公式APIへ接続済み · {account.PlanType}"
                        : "OpenAI公式APIへ接続済み",
                    account.Email);
                _codexSnapshot = rateLimits.Windows.Count == 0
                    ? new TokenUsageSnapshot(
                        "Codex",
                        [],
                        account.Email,
                        account.PlanType,
                        now,
                        TokenAvailabilityReason.NoData)
                    : new TokenUsageSnapshot(
                        "Codex",
                        rateLimits.Windows,
                        account.Email,
                        account.PlanType,
                        now);
                _nextCodexRefreshUtc = now + SuccessfulRefreshInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsServiceFailure(ex))
            {
                var serviceUnavailable = ex is Win32Exception or FileNotFoundException;
                _codexConnection = new LlmConnectionSnapshot(
                    "Codex",
                    serviceUnavailable
                        ? LlmConnectionStatus.ServiceUnavailable
                        : LlmConnectionStatus.NetworkError,
                    serviceUnavailable
                        ? "Codex App Serverを起動できません。Codexのインストールを確認してください"
                        : "OpenAI公式APIから使用率を取得できません。自動で再試行します");
                _codexSnapshot = TokenUsageSnapshot.Unavailable(
                    "Codex",
                    serviceUnavailable
                        ? TokenAvailabilityReason.ServiceUnavailable
                        : TokenAvailabilityReason.NetworkError);
                _nextCodexRefreshUtc = now + FailedRefreshInterval;
            }
        }
        finally
        {
            _codexRefreshGate.Release();
        }
    }

    private async Task RefreshClaudeAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh && now < _nextClaudeRefreshUtc)
        {
            return;
        }

        await _claudeRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (!forceRefresh && now < _nextClaudeRefreshUtc)
            {
                return;
            }

            try
            {
                if (!_claudeClient.HasSessionKey())
                {
                    _claudeConnection = ClaudeAuthenticationRequired();
                    _claudeSnapshot = TokenUsageSnapshot.Unavailable(
                        "Claude",
                        TokenAvailabilityReason.AuthenticationRequired);
                    _nextClaudeRefreshUtc = now + SuccessfulRefreshInterval;
                    return;
                }

                var usage = await _claudeClient.ReadUsageAsync(cancellationToken).ConfigureAwait(false);
                ApplyClaudeUsage(usage, now);
                _nextClaudeRefreshUtc = now + SuccessfulRefreshInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ClaudeWebUsageException ex)
            {
                ApplyClaudeFailure(ex, now);
            }
            catch (ArgumentException)
            {
                _claudeConnection = ClaudeAuthenticationRequired(
                    "保存済みのClaude Session Token形式を確認してください");
                _claudeSnapshot = TokenUsageSnapshot.Unavailable(
                    "Claude",
                    TokenAvailabilityReason.AuthenticationRequired);
                _nextClaudeRefreshUtc = DateTimeOffset.MaxValue;
            }
            catch (Exception ex) when (IsServiceFailure(ex))
            {
                ApplyClaudeStorageFailure(now);
            }
        }
        finally
        {
            _claudeRefreshGate.Release();
        }
    }

    private void ApplyClaudeUsage(ClaudeWebUsageResult usage, DateTimeOffset now)
    {
        _claudeConnection = new LlmConnectionSnapshot(
            "Claude",
            LlmConnectionStatus.Connected,
            "claude.ai公式Web応答へ接続済み",
            usage.OrganizationName);
        _claudeSnapshot = usage.Windows.Count == 0
            ? new TokenUsageSnapshot(
                "Claude",
                [],
                usage.OrganizationName,
                UpdatedAt: now,
                AvailabilityReason: TokenAvailabilityReason.NoData)
            : new TokenUsageSnapshot(
                "Claude",
                usage.Windows,
                usage.OrganizationName,
                UpdatedAt: now);
    }

    private void ApplyClaudeFailure(ClaudeWebUsageException exception, DateTimeOffset now)
    {
        var status = exception.FailureKind switch
        {
            ClaudeWebFailureKind.AuthenticationRequired => LlmConnectionStatus.AuthenticationRequired,
            ClaudeWebFailureKind.NetworkError => LlmConnectionStatus.NetworkError,
            _ => LlmConnectionStatus.ServiceUnavailable,
        };
        var reason = exception.FailureKind switch
        {
            ClaudeWebFailureKind.AuthenticationRequired => TokenAvailabilityReason.AuthenticationRequired,
            ClaudeWebFailureKind.NetworkError => TokenAvailabilityReason.NetworkError,
            _ => TokenAvailabilityReason.ServiceUnavailable,
        };
        var description = exception.FailureKind switch
        {
            ClaudeWebFailureKind.AuthenticationRequired
                => "有効なClaude Session Tokenを貼り付けて再接続してください",
            ClaudeWebFailureKind.NetworkError
                => "claude.aiから使用率を取得できません。自動で再試行します",
            _ => "claude.aiの非公開Web応答が変更された可能性があります",
        };

        _claudeConnection = new LlmConnectionSnapshot("Claude", status, description);
        _claudeSnapshot = TokenUsageSnapshot.Unavailable("Claude", reason);

        // 失効・不正なSession Tokenは再試行しても同じ結果なので、再接続まで自動送信を止める
        _nextClaudeRefreshUtc = status == LlmConnectionStatus.AuthenticationRequired
            ? DateTimeOffset.MaxValue
            : now + FailedRefreshInterval;
    }

    private void ApplyClaudeStorageFailure(DateTimeOffset now)
    {
        _claudeConnection = new LlmConnectionSnapshot(
            "Claude",
            LlmConnectionStatus.ServiceUnavailable,
            "ClaudeのSession TokenをWindows資格情報から読み書きできません");
        _claudeSnapshot = TokenUsageSnapshot.Unavailable(
            "Claude",
            TokenAvailabilityReason.ServiceUnavailable);
        _nextClaudeRefreshUtc = now + FailedRefreshInterval;
    }

    // InvalidDataException は IOException の派生ではないため個別に列挙する
    private static bool IsServiceFailure(Exception exception) => exception is
        IOException
        or InvalidDataException
        or Win32Exception
        or JsonException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or TimeoutException
        or InvalidOperationException
        or NotSupportedException;

    private static LlmConnectionSnapshot AuthenticationRequired(
        string description = "設定からOpenAIアカウントへWeb認証してください")
        => new(
            "Codex",
            LlmConnectionStatus.AuthenticationRequired,
            description);

    private static LlmConnectionSnapshot ClaudeAuthenticationRequired(
        string description = "ClaudeのSession Tokenを設定してください")
        => new(
            "Claude",
            LlmConnectionStatus.AuthenticationRequired,
            description);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _codexClient.Dispose();
        _claudeClient.Dispose();
        _codexRefreshGate.Dispose();
        _claudeRefreshGate.Dispose();
    }
}
