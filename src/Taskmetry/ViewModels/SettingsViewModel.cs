using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taskmetry.Models;
using Taskmetry.Services;

namespace Taskmetry.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly IDataFolderService _dataFolderService;

    public SettingsViewModel(
        SettingsService settingsService,
        IStartupService startupService,
        IDataFolderService dataFolderService)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        _dataFolderService = dataFolderService;
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
        ClaudeContextLimit = settings.ClaudeContextLimit;
        GeminiContextLimit = settings.GeminiContextLimit;
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
    [ObservableProperty] private long _claudeContextLimit;
    [ObservableProperty] private long _geminiContextLimit;
    [ObservableProperty] private string _statusMessage = "設定はこのPC内だけに保存されます";

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
                ClaudeContextLimit = ClaudeContextLimit,
                GeminiContextLimit = GeminiContextLimit,
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
        _ => "設定はこのPC内だけに保存されます",
    };
}
