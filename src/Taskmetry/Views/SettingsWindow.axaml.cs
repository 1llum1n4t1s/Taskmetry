using Avalonia.Controls;
using Avalonia.Threading;
using Taskmetry.ViewModels;

namespace Taskmetry.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
        : this(CreateDefaultViewModel())
    {
    }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _viewModel.CloseRequested += OnCloseRequested;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => SettingsScroll.Offset = default, DispatcherPriority.Background);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Opened -= OnOpened;
        Closed -= OnClosed;
    }

    private static SettingsViewModel CreateDefaultViewModel()
    {
        var settingsService = new Taskmetry.Services.SettingsService();
        _ = settingsService.Load();
        return new SettingsViewModel(settingsService);
    }
}
