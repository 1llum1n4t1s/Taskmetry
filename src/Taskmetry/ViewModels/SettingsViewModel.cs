using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taskmetry.Models;
using Taskmetry.Services;

namespace Taskmetry.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
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
        try
        {
            StartupService.SetEnabled(StartWithWindows);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusMessage = $"保存できませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenDataFolder()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Taskmetry");
        Directory.CreateDirectory(directory);
        _ = Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ResetPosition()
    {
        var settings = _settingsService.Current.Clone();
        settings.ManualOffsetPixels = 0;
        _settingsService.Save(settings);
        StatusMessage = "手動位置を中央基準へ戻しました";
    }
}
