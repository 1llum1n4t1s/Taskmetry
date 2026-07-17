using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Taskmetry.Models;
using Taskmetry.Services;

namespace Taskmetry.ViewModels;

public sealed partial class TaskbarViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly SystemMetricsService _systemMetricsService;
    private readonly TokenUsageService _tokenUsageService;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly MetricItemViewModel _cpu;
    private readonly MetricItemViewModel _memory;
    private readonly MetricItemViewModel _codex;
    private readonly MetricItemViewModel _claude;
    private readonly MetricItemViewModel _gemini;
    private AppSettings _settings;
    private Task? _refreshTask;

    public TaskbarViewModel(
        SettingsService settingsService,
        SystemMetricsService systemMetricsService,
        TokenUsageService tokenUsageService)
    {
        _settingsService = settingsService;
        _systemMetricsService = systemMetricsService;
        _tokenUsageService = tokenUsageService;
        _settings = settingsService.Current.Clone();

        _cpu = new MetricItemViewModel("cpu", "CPU", "#3FD8FF");
        _memory = new MetricItemViewModel("memory", "RAM", "#A88BFF");
        _codex = new MetricItemViewModel("codex", "CODEX", "#67A4FF");
        _claude = new MetricItemViewModel("claude", "CLAUDE", "#FFC857");
        _gemini = new MetricItemViewModel("gemini", "GEMINI", "#62E6A8");
        Metrics = [];
        ApplyVisibility();

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public ObservableCollection<MetricItemViewModel> Metrics { get; }

    public int PreferredWidthPixels => _settings.PreferredWidthPixels;
    public bool IsLayoutEditMode => _settings.LayoutEditMode;
    public RailPlacementMode PlacementMode => _settings.PlacementMode;
    public int ManualOffsetPixels => _settings.ManualOffsetPixels;

    [ObservableProperty]
    private bool _isVertical;

    [ObservableProperty]
    private bool _isCompact;

    [ObservableProperty]
    private string _layoutHint = "固定表示 · 自動配置";

    public bool IsHorizontal => !IsVertical;

    public event EventHandler? LayoutSettingsChanged;

    public void Start()
    {
        _refreshTask ??= RefreshLoopAsync(_cancellation.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var metrics = _systemMetricsService.Read();
                var tokenUsage = await _tokenUsageService.ReadAllAsync(_settings, cancellationToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => Apply(metrics, tokenUsage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _cpu.DetailText = "システム計測を再試行中";
                    _memory.DetailText = "システム計測を再試行中";
                });
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Apply(MetricSnapshot metrics, IReadOnlyDictionary<string, TokenUsageSnapshot> tokenUsage)
    {
        _cpu.SetPercent(metrics.CpuPercent);
        _cpu.DetailText = $"CPU 使用率 {metrics.CpuPercent:0.0}%";

        _memory.SetPercent(metrics.MemoryPercent);
        _memory.DetailText = $"メモリ {FormatBytes(metrics.UsedMemoryBytes)} / {FormatBytes(metrics.TotalMemoryBytes)}";

        ApplyTokenMetric(_codex, tokenUsage["Codex"]);
        ApplyTokenMetric(_claude, tokenUsage["Claude"]);
        ApplyTokenMetric(_gemini, tokenUsage["Gemini"]);
    }

    private static void ApplyTokenMetric(MetricItemViewModel item, TokenUsageSnapshot snapshot)
    {
        if (!snapshot.IsAvailable)
        {
            item.SetPercent(null);
            item.DetailText = $"{snapshot.Provider} のセッション記録が見つかりません";
            return;
        }

        item.SetPercent(snapshot.ContextPercent);
        var detail = $"コンテキスト {FormatTokens(snapshot.UsedTokens)} / {FormatTokens(snapshot.ContextLimit)}";
        if (!string.IsNullOrWhiteSpace(snapshot.Model))
        {
            detail += $" · {snapshot.Model}";
        }

        if (snapshot.RateLimitPercent is { } rateLimit)
        {
            var window = snapshot.RateLimitWindowMinutes is { } minutes
                ? minutes >= 1_440 ? $"{minutes / 1_440}日枠" : $"{minutes}分枠"
                : "利用枠";
            detail += $" · {window} {rateLimit:0}%";
        }

        item.DetailText = detail;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _settings = settings.Clone();
        Dispatcher.UIThread.Post(() =>
        {
            ApplyVisibility();
            OnPropertyChanged(nameof(PreferredWidthPixels));
            OnPropertyChanged(nameof(IsLayoutEditMode));
            OnPropertyChanged(nameof(PlacementMode));
            OnPropertyChanged(nameof(ManualOffsetPixels));
            LayoutSettingsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void ApplyPlacement(TaskbarPlacementService.PlacementResult placement)
    {
        IsVertical = placement.IsVertical;
        OnPropertyChanged(nameof(IsHorizontal));
        IsCompact = placement.IsVertical || placement.Width < 560;
        var mode = IsLayoutEditMode ? "編集モード · ドラッグで移動" : "固定表示 · クリック透過";
        var location = placement.IsOutside ? "タスクバー外側" : "空きスペース";
        LayoutHint = $"{mode} · {location}";
    }

    public void SaveManualOffset(int offsetPixels)
    {
        var settings = _settings.Clone();
        settings.ManualOffsetPixels = offsetPixels;
        _settingsService.Save(settings);
    }

    private void ApplyVisibility()
    {
        Metrics.Clear();
        AddIfVisible(_cpu, _settings.ShowCpu);
        AddIfVisible(_memory, _settings.ShowMemory);
        AddIfVisible(_codex, _settings.ShowCodex);
        AddIfVisible(_claude, _settings.ShowClaude);
        AddIfVisible(_gemini, _settings.ShowGemini);
    }

    private void AddIfVisible(MetricItemViewModel item, bool isVisible)
    {
        item.IsVisible = isVisible;
        if (isVisible)
        {
            Metrics.Add(item);
        }
    }

    internal static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000d:0.##}M",
        >= 1_000 => $"{tokens / 1_000d:0.#}K",
        _ => tokens.ToString("N0"),
    };

    private static string FormatBytes(ulong bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
