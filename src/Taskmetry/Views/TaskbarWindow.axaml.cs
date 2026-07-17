using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Taskmetry.Services;
using Taskmetry.ViewModels;

namespace Taskmetry.Views;

public sealed partial class TaskbarWindow : Window
{
    private readonly TaskbarPlacementService _placementService;
    private readonly TaskbarViewModel _viewModel;
    private readonly Action _showSettings;
    private readonly DispatcherTimer _placementTimer;
    private bool _isDragging;

    public TaskbarWindow()
        : this(new TaskbarPlacementService(), CreateDefaultViewModel(), static () => { })
    {
    }

    public TaskbarWindow(
        TaskbarPlacementService placementService,
        TaskbarViewModel viewModel,
        Action showSettings)
    {
        _placementService = placementService;
        _viewModel = viewModel;
        _showSettings = showSettings;
        DataContext = viewModel;

        InitializeComponent();

        _placementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _placementTimer.Tick += (_, _) => PlaceSafely();
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.LayoutSettingsChanged += OnLayoutSettingsChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _placementService.ConfigureWindow(this, _viewModel.IsLayoutEditMode);
        PlaceSafely();
        _placementTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _placementTimer.Stop();
        _viewModel.LayoutSettingsChanged -= OnLayoutSettingsChanged;
    }

    private void OnLayoutSettingsChanged(object? sender, EventArgs e)
    {
        _placementService.ConfigureWindow(this, _viewModel.IsLayoutEditMode);
        PlaceSafely();
    }

    private void PlaceSafely()
    {
        if (_isDragging)
        {
            return;
        }

        try
        {
            if (_placementService.Place(
                    this,
                    _viewModel.PreferredWidthPixels,
                    _viewModel.PlacementMode,
                    _viewModel.ManualOffsetPixels,
                    out var placement))
            {
                _viewModel.ApplyPlacement(placement);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer 再起動中は次のタイマーで再配置する。
        }
    }

    private void OnRailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_viewModel.IsLayoutEditMode && point.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            BeginMoveDrag(e);
            e.Handled = true;
        }
        else if (_viewModel.IsLayoutEditMode && point.Properties.IsRightButtonPressed)
        {
            _showSettings();
            e.Handled = true;
        }
    }

    private void OnRailPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        if (_placementService.TryCalculateCurrentPlacement(
                _viewModel.PreferredWidthPixels,
                _viewModel.PlacementMode,
                0,
                out var basePlacement))
        {
            var currentMain = basePlacement.IsVertical ? Position.Y : Position.X;
            _viewModel.SaveManualOffset(currentMain - basePlacement.BaseMainPosition);
        }

        PlaceSafely();
        e.Handled = true;
    }

    private static TaskbarViewModel CreateDefaultViewModel()
    {
        var settingsService = new SettingsService();
        _ = settingsService.Load();
        return new TaskbarViewModel(settingsService, new SystemMetricsService(), new TokenUsageService());
    }
}
