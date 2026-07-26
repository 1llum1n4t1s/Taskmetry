using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taskmetry.Models;
using Taskmetry.Services;

namespace Taskmetry.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly IDataFolderService _dataFolderService;
    private readonly ILlmUsageService _llmUsageService;
    private readonly IExternalBrowserService _externalBrowserService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _ownsLlmUsageService;
    private bool _disposed;
    private bool _codexConnected;
    private bool _claudeConnected;

    public SettingsViewModel(
        SettingsService settingsService,
        IStartupService startupService,
        IDataFolderService dataFolderService)
        : this(
            settingsService,
            startupService,
            dataFolderService,
            new TokenUsageService(),
            new ExternalBrowserService())
    {
        _ownsLlmUsageService = true;
    }

    public SettingsViewModel(
        SettingsService settingsService,
        IStartupService startupService,
        IDataFolderService dataFolderService,
        ILlmUsageService llmUsageService,
        IExternalBrowserService externalBrowserService)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        _dataFolderService = dataFolderService;
        _llmUsageService = llmUsageService;
        _externalBrowserService = externalBrowserService;
        var settings = settingsService.Current;
        ShowCpu = settings.ShowCpu;
        ShowMemory = settings.ShowMemory;
        ShowCodex = settings.ShowCodex;
        ShowClaude = settings.ShowClaude;
        ShowGemini = settings.ShowGemini;
        StartWithWindows = settings.StartWithWindows;
        LayoutEditMode = settings.LayoutEditMode;
        PlacementMode = (int)settings.PlacementMode;
        PreferredWidthPixels = settings.PreferredWidthPixels;
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        StatusMessage = GetLoadStatusMessage(settingsService.LastLoadResult);
    }

    [ObservableProperty] private bool _showCpu;
    [ObservableProperty] private bool _showMemory;
    [ObservableProperty] private bool _showCodex;
    [ObservableProperty] private bool _showClaude;
    [ObservableProperty] private bool _showGemini;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _layoutEditMode;
    [ObservableProperty] private int _placementMode;
    [ObservableProperty] private int _preferredWidthPixels;
    [ObservableProperty] private int _refreshIntervalSeconds;
    [ObservableProperty] private string _statusMessage = "設定はこのPC内だけに保存されます";
    [ObservableProperty] private string _codexConnectionText = "接続状態を確認しています…";
    [ObservableProperty] private string _codexActionText = "Webで接続";
    [ObservableProperty] private bool _isCodexActionEnabled;
    [ObservableProperty] private string _claudeConnectionText = "接続状態を確認しています…";
    [ObservableProperty] private string _claudeActionText = "Session Tokenで接続";
    [ObservableProperty] private bool _isClaudeActionEnabled;
    [ObservableProperty] private bool _isClaudeSessionInputVisible = true;
    [ObservableProperty] private string _claudeSessionKey = string.Empty;
    [ObservableProperty] private string _geminiConnectionText = "公式APIの提供状況を確認しています…";

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Save()
    {
        var startupStateCaptured = false;
        var previousStartupState = false;
        try
        {
            previousStartupState = _startupService.IsEnabled();
            startupStateCaptured = true;
            _startupService.SetEnabled(StartWithWindows);
            _settingsService.Save(new AppSettings
            {
                FirstRun = false,
                ShowCpu = ShowCpu,
                ShowMemory = ShowMemory,
                ShowCodex = ShowCodex,
                ShowClaude = ShowClaude,
                ShowGemini = ShowGemini,
                StartWithWindows = StartWithWindows,
                LayoutEditMode = LayoutEditMode,
                PlacementMode = (RailPlacementMode)PlacementMode,
                ManualOffsetPixels = _settingsService.Current.ManualOffsetPixels,
                PreferredWidthPixels = PreferredWidthPixels,
                RefreshIntervalSeconds = RefreshIntervalSeconds,
            });
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (SettingsService.IsPersistenceException(ex))
        {
            var rollbackFailed = false;
            if (startupStateCaptured)
            {
                try
                {
                    _startupService.SetEnabled(previousStartupState);
                }
                catch (Exception rollbackException) when (SettingsService.IsPersistenceException(rollbackException))
                {
                    rollbackFailed = true;
                }
            }

            StatusMessage = rollbackFailed
                ? "設定を保存できず、自動起動設定の復元にも失敗しました"
                : $"保存できませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            _dataFolderService.Open();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"保存先を開けませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetPosition()
    {
        try
        {
            var settings = _settingsService.Current.Clone();
            settings.ManualOffsetPixels = 0;
            _settingsService.Save(settings);
            StatusMessage = "手動位置を中央基準へ戻しました";
        }
        catch (Exception ex) when (SettingsService.IsPersistenceException(ex))
        {
            StatusMessage = $"位置をリセットできませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshConnectionsAsync()
    {
        IsCodexActionEnabled = false;
        IsClaudeActionEnabled = false;
        try
        {
            var states = await _llmUsageService.ReadConnectionStatesAsync(
                forceRefresh: true,
                _lifetimeCancellation.Token);
            ApplyConnectionStates(states);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsConnectionException(ex))
        {
            _codexConnected = false;
            CodexConnectionText = "接続状態を確認できませんでした";
            CodexActionText = "再試行";
            _claudeConnected = false;
            ClaudeConnectionText = "接続状態を確認できませんでした";
            ClaudeActionText = "再試行";
            IsClaudeSessionInputVisible = true;
            StatusMessage = $"公式サービスへの接続確認に失敗しました: {ex.Message}";
        }
        finally
        {
            IsCodexActionEnabled = true;
            IsClaudeActionEnabled = true;
        }
    }

    [RelayCommand]
    private async Task ToggleCodexConnectionAsync()
    {
        IsCodexActionEnabled = false;
        try
        {
            if (_codexConnected)
            {
                var disconnected = await _llmUsageService.DisconnectCodexAsync(_lifetimeCancellation.Token);
                ApplyCodexConnection(disconnected);
                StatusMessage = "OpenAIアカウントとの接続を解除しました";
                return;
            }

            var login = await _llmUsageService.BeginCodexLoginAsync(_lifetimeCancellation.Token);
            CodexConnectionText = "ブラウザーでOpenAI認証を完了してください";
            CodexActionText = "認証待ち";
            _externalBrowserService.Open(login.AuthenticationUri);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var connection = await _llmUsageService.CompleteCodexLoginAsync(login.LoginId, timeout.Token);
            ApplyCodexConnection(connection);
            StatusMessage = connection.IsConnected
                ? "Codexの公式使用率を定期取得します"
                : connection.Description;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            CodexConnectionText = "認証待ちがタイムアウトしました。接続状態を再確認してください";
            CodexActionText = "再確認";
            StatusMessage = "OpenAI認証の完了を確認できませんでした";
        }
        catch (Exception ex) when (IsConnectionException(ex))
        {
            _codexConnected = false;
            CodexConnectionText = "OpenAIのWeb認証を開始できませんでした";
            CodexActionText = "再試行";
            StatusMessage = $"Codex接続エラー: {ex.Message}";
        }
        finally
        {
            IsCodexActionEnabled = true;
        }
    }

    [RelayCommand]
    private async Task ToggleClaudeConnectionAsync()
    {
        IsClaudeActionEnabled = false;
        try
        {
            if (_claudeConnected)
            {
                var disconnected = await _llmUsageService
                    .DisconnectClaudeAsync(_lifetimeCancellation.Token);
                ApplyClaudeConnection(disconnected);
                StatusMessage = "Claudeとの接続を解除し、保存したSession Tokenを削除しました";
                return;
            }

            if (string.IsNullOrWhiteSpace(ClaudeSessionKey))
            {
                ClaudeConnectionText = "sk-ant-sid01- で始まるSession Tokenを貼り付けてください";
                StatusMessage = "ClaudeのSession Tokenはパスワードと同様に取り扱ってください";
                return;
            }

            ClaudeConnectionText = "claude.aiでSession Tokenを確認しています…";
            var connection = await _llmUsageService.ConnectClaudeAsync(
                ClaudeSessionKey,
                _lifetimeCancellation.Token);
            ApplyClaudeConnection(connection);
            StatusMessage = connection.IsConnected
                ? "Claudeの公式Web使用率を60秒ごとに取得します"
                : connection.Description;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (ArgumentException)
        {
            _claudeConnected = false;
            IsClaudeSessionInputVisible = true;
            ClaudeConnectionText = "sk-ant-sid01- で始まるSession Tokenを確認してください";
            ClaudeActionText = "Session Tokenで接続";
            StatusMessage = "ClaudeのSession Token形式が正しくありません";
        }
        catch (Exception ex) when (IsConnectionException(ex))
        {
            _claudeConnected = false;
            IsClaudeSessionInputVisible = true;
            ClaudeConnectionText = "Claudeへ接続できませんでした";
            ClaudeActionText = "再試行";
            StatusMessage = $"Claude接続エラー: {ex.Message}";
        }
        finally
        {
            ClaudeSessionKey = string.Empty;
            IsClaudeActionEnabled = true;
        }
    }

    private void ApplyConnectionStates(IReadOnlyDictionary<string, LlmConnectionSnapshot> states)
    {
        ApplyCodexConnection(states["Codex"]);
        ApplyClaudeConnection(states["Claude"]);
        GeminiConnectionText = states["Gemini"].Description;
    }

    private void ApplyCodexConnection(LlmConnectionSnapshot connection)
    {
        _codexConnected = connection.IsConnected;
        CodexConnectionText = string.IsNullOrWhiteSpace(connection.AccountLabel)
            ? connection.Description
            : $"{connection.Description} · {MaskAccountLabel(connection.AccountLabel)}";
        CodexActionText = connection.Status switch
        {
            LlmConnectionStatus.Connected => "接続を解除",
            LlmConnectionStatus.AuthenticationInProgress => "認証待ち",
            LlmConnectionStatus.NetworkError => "再確認",
            LlmConnectionStatus.ServiceUnavailable => "再確認",
            _ => "Webで接続",
        };
    }

    private void ApplyClaudeConnection(LlmConnectionSnapshot connection)
    {
        _claudeConnected = connection.IsConnected;
        ClaudeConnectionText = string.IsNullOrWhiteSpace(connection.AccountLabel)
            ? connection.Description
            : $"{connection.Description} · {MaskAccountLabel(connection.AccountLabel)}";
        ClaudeActionText = connection.Status switch
        {
            LlmConnectionStatus.Connected => "接続を解除",
            LlmConnectionStatus.NetworkError => "再試行",
            LlmConnectionStatus.ServiceUnavailable => "再試行",
            _ => "Session Tokenで接続",
        };
        IsClaudeSessionInputVisible = !connection.IsConnected;
    }

    private static bool IsConnectionException(Exception exception) => exception is
        IOException
        or System.ComponentModel.Win32Exception
        or System.Text.Json.JsonException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or TimeoutException
        or InvalidOperationException
        or NotSupportedException;

    internal static string MaskAccountLabel(string accountLabel)
    {
        var separator = accountLabel.IndexOf('@');
        if (separator <= 0 || separator == accountLabel.Length - 1)
        {
            return accountLabel;
        }

        return $"{accountLabel[0]}***{accountLabel[separator..]}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        if (_ownsLlmUsageService)
        {
            _llmUsageService.Dispose();
        }
    }

    private static string GetLoadStatusMessage(SettingsLoadResult result) => result.Status switch
    {
        SettingsLoadStatus.Corrupt when result.RecoveryCopyCreated
            => "破損した設定を退避し、既定値で開きました",
        SettingsLoadStatus.Corrupt
            => "設定が破損し退避できなかったため、保存を停止しています",
        SettingsLoadStatus.IoError
            => "設定を読み取れなかったため、既存設定を保護して保存を停止しています",
        SettingsLoadStatus.AccessDenied
            => "設定を読み取る権限がないため、保存を停止しています",
        _ => "設定はこのPC内に保存し、AI使用率は公式Web/APIだけから取得します",
    };
}
