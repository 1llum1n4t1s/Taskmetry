using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Taskmetry.Services;
using Taskmetry.ViewModels;
using Taskmetry.Views;

namespace Taskmetry;

public sealed partial class App : Application
{
    private SettingsService? _settingsService;
    private TaskbarViewModel? _taskbarViewModel;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settingsService = new SettingsService();
            var settings = _settingsService.Load();
            _taskbarViewModel = new TaskbarViewModel(
                _settingsService,
                new SystemMetricsService(),
                new TokenUsageService());

            var taskbarWindow = new TaskbarWindow(
                new TaskbarPlacementService(),
                _taskbarViewModel,
                ShowSettings);
            desktop.MainWindow = taskbarWindow;
            desktop.Exit += OnDesktopExit;

            CreateTrayIcon();
            _taskbarViewModel.Start();

            if (settings.FirstRun)
            {
                Dispatcher.UIThread.Post(ShowSettings, DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Taskmetry/icon/app_icon.png"));
        var openItem = new NativeMenuItem("設定を開く");
        openItem.Click += (_, _) => ShowSettings();
        var exitItem = new NativeMenuItem("終了");
        exitItem.Click += (_, _) => _desktop?.Shutdown();

        var menu = new NativeMenu();
        menu.Add(openItem);
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

    private void ShowSettings()
    {
        if (_settingsService is null)
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

        var viewModel = new SettingsViewModel(_settingsService);
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _taskbarViewModel?.Dispose();
        _taskbarViewModel = null;
    }
}
