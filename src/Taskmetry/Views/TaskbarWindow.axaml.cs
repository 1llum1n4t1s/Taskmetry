using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Taskmetry.Models;
using Taskmetry.Services;
using Taskmetry.ViewModels;

namespace Taskmetry.Views;

public sealed partial class TaskbarWindow : Window
{
    private readonly TaskbarPlacementService _placementService;
    private readonly TaskbarViewModel _viewModel;
    private readonly Action _showSettings;
    private readonly RailSide _slot;
    private readonly DispatcherTimer _placementTimer;
    private bool _isDragging;

    public TaskbarWindow()
        : this(new TaskbarPlacementService(), CreateDefaultViewModel(), static () => { })
    {
    }

    public TaskbarWindow(
        TaskbarPlacementService placementService,
        TaskbarViewModel viewModel,
        Action showSettings,
        RailSide slot = RailSide.Right)
    {
        _placementService = placementService;
        _viewModel = viewModel;
        _showSettings = showSettings;
        _slot = slot;
        DataContext = viewModel;

        InitializeComponent();

        var metrics = _viewModel.MetricsFor(_slot);
        HorizontalMetrics.ItemsSource = metrics;
        VerticalMetrics.ItemsSource = metrics;

        _placementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _placementTimer.Tick += (_, _) => PlaceSafely();
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.LayoutSettingsChanged += OnLayoutSettingsChanged;
        _viewModel.RailCompositionChanged += OnRailCompositionChanged;
    }

    /// <summary>担当スロットにメーターが 1 つも無ければウィンドウごと隠す。</summary>
    public void ApplyRailComposition()
    {
        if (_viewModel.IsRailVisible(_slot))
        {
            if (!IsVisible)
            {
                Show();
            }

            PlaceSafely();
            return;
        }

        if (IsVisible)
        {
            Hide();
        }
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
        _viewModel.RailCompositionChanged -= OnRailCompositionChanged;
    }

    private void OnLayoutSettingsChanged(object? sender, EventArgs e)
    {
        _placementService.ConfigureWindow(this, _viewModel.IsLayoutEditMode);
        PlaceSafely();
    }

    private void OnRailCompositionChanged(object? sender, EventArgs e) => ApplyRailComposition();

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
                    _viewModel.PreferredWidthFor(_slot),
                    _viewModel.PlacementMode,
                    _viewModel.ManualOffsetPixels,
                    _slot,
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

        // 左右分割中は各レールが担当する空き領域の端へ固定されるためドラッグ移動は行わない
        if (_viewModel.IsLayoutEditMode && !_viewModel.IsSplitRail && point.Properties.IsLeftButtonPressed)
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
                _viewModel.PreferredWidthFor(_slot),
                _viewModel.PlacementMode,
                0,
                _slot,
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
