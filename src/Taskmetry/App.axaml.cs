using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Taskmetry.Models;
using Taskmetry.Services;
using Taskmetry.ViewModels;
using Taskmetry.Views;

namespace Taskmetry;

public sealed partial class App : Application
{
    private SettingsService? _settingsService;
    private IStartupService? _startupService;
    private IDataFolderService? _dataFolderService;
    private ILlmUsageService? _llmUsageService;
    private TaskbarViewModel? _taskbarViewModel;
    private TaskbarWindow? _leftRailWindow;
    private TaskbarWindow? _rightRailWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private UpdateService? _updateService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settingsService = new SettingsService();
            _startupService = new StartupService();
            _dataFolderService = new DataFolderService();
            _llmUsageService = new TokenUsageService();
            _updateService = new UpdateService();
            var loadResult = _settingsService.Load();
            var settings = loadResult.Settings;
            _taskbarViewModel = new TaskbarViewModel(
                _settingsService,
                new SystemMetricsService(),
                _llmUsageService);

            // アイコン群の左右それぞれの空きを 1 レール 1 ウィンドウで担当する
            var placementService = new TaskbarPlacementService();
            _rightRailWindow = new TaskbarWindow(
                placementService,
                _taskbarViewModel,
                ShowSettings,
                RailSide.Right);
            _leftRailWindow = new TaskbarWindow(
                placementService,
                _taskbarViewModel,
                ShowSettings,
                RailSide.Left);
            desktop.MainWindow = _rightRailWindow;
            desktop.Exit += OnDesktopExit;

            CreateTrayIcon();
            _taskbarViewModel.Start();
            _leftRailWindow.Show();
            _leftRailWindow.ApplyRailComposition();
            _rightRailWindow.ApplyRailComposition();

            if (settings.FirstRun)
            {
                Dispatcher.UIThread.Post(ShowSettings, DispatcherPriority.Background);
            }
            else
            {
                Dispatcher.UIThread.Post(
                    () => _ = CheckForUpdatesAsync(manualCheck: false),
                    DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Taskmetry/icon/app_icon.png"));
        var openItem = new NativeMenuItem("設定を開く");
        openItem.Click += (_, _) => ShowSettings();
        var updateItem = new NativeMenuItem("更新を確認");
        updateItem.Click += (_, _) => _ = CheckForUpdatesAsync(manualCheck: true);
        var exitItem = new NativeMenuItem("終了");
        exitItem.Click += (_, _) => _desktop?.Shutdown();

        var menu = new NativeMenu();
        menu.Add(openItem);
        menu.Add(updateItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Taskmetry — Live Rail",
            Icon = new WindowIcon(iconStream),
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ShowSettings();
    }

    private async Task CheckForUpdatesAsync(bool manualCheck)
    {
        if (_updateService is null || _desktop?.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            await _updateService.CheckAsync(owner, manualCheck, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // アプリ終了に伴うキャンセルは正常系。
        }
    }

    private void ShowSettings()
    {
        if (_settingsService is null
            || _startupService is null
            || _dataFolderService is null
            || _llmUsageService is null)
        {
            return;
        }

        if (_settingsWindow is { } existing)
        {
            if (!existing.IsVisible)
            {
                existing.Show();
            }

            existing.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(
            _settingsService,
            _startupService,
            _dataFolderService,
            _llmUsageService,
            new ExternalBrowserService());
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _leftRailWindow?.Close();
        _leftRailWindow = null;
        _rightRailWindow = null;
        _taskbarViewModel?.Dispose();
        _taskbarViewModel = null;
        _llmUsageService = null;
        _updateService = null;
        _lifetimeCancellation.Dispose();
    }
}
