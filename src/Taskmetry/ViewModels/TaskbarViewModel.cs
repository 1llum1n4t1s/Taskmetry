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
    private readonly ILlmUsageService _tokenUsageService;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly MetricItemViewModel _cpu;
    private readonly MetricItemViewModel _memory;
    private readonly MetricItemViewModel _codex;
    private readonly MetricItemViewModel _claude;
    private readonly MetricItemViewModel _gemini;
    private AppSettings _settings;
    private Task? _refreshTask;
    private bool _manualOffsetSaveFailed;
    private bool _disposed;

    public TaskbarViewModel(
        SettingsService settingsService,
        SystemMetricsService systemMetricsService,
        ILlmUsageService tokenUsageService)
    {
        _settingsService = settingsService;
        _systemMetricsService = systemMetricsService;
        _tokenUsageService = tokenUsageService;
        _settings = settingsService.Current.Clone();

        _cpu = new MetricItemViewModel("CPU", "#3FD8FF");
        _memory = new MetricItemViewModel("RAM", "#A88BFF");
        _codex = new MetricItemViewModel("CODEX", "#67A4FF");
        _claude = new MetricItemViewModel("CLAUDE", "#FFC857");
        _gemini = new MetricItemViewModel("GEMINI", "#62E6A8");
        LeftMetrics = [];
        RightMetrics = [];
        ApplyVisibility();

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>アイコン群より手前（横置きなら左、縦置きなら上）の空きに並ぶメーター。</summary>
    public ObservableCollection<MetricItemViewModel> LeftMetrics { get; }

    /// <summary>アイコン群と通知領域の間の空きに並ぶメーター。分割OFFでは全メーターがここへ入る。</summary>
    public ObservableCollection<MetricItemViewModel> RightMetrics { get; }

    public int PreferredWidthPixels => _settings.PreferredWidthPixels;
    public bool IsLayoutEditMode => _settings.LayoutEditMode;
    public RailPlacementMode PlacementMode => _settings.PlacementMode;
    public int ManualOffsetPixels => _settings.ManualOffsetPixels;
    public bool IsSplitRail => _settings.SplitRail;

    public ObservableCollection<MetricItemViewModel> MetricsFor(RailSide slot)
        => slot == RailSide.Left ? LeftMetrics : RightMetrics;

    public bool IsRailVisible(RailSide slot) => MetricsFor(slot).Count > 0;

    /// <summary>
    /// スロットごとの希望幅。分割時はメーター数で表示幅を按分し、
    /// 左右でメーター 1 枚あたりの幅がそろうようにする。
    /// </summary>
    public int PreferredWidthFor(RailSide slot)
    {
        var slotCount = MetricsFor(slot).Count;
        var totalCount = LeftMetrics.Count + RightMetrics.Count;
        if (!IsSplitRail || slotCount == 0 || totalCount == 0)
        {
            return _settings.PreferredWidthPixels;
        }

        var perMetric = _settings.PreferredWidthPixels / (double)totalCount;
        return (int)Math.Round(perMetric * slotCount);
    }

    [ObservableProperty]
    private bool _isVertical;

    [ObservableProperty]
    private string _layoutHint = "固定表示 · 自動配置";

    public bool IsHorizontal => !IsVertical;

    public event EventHandler? LayoutSettingsChanged;

    /// <summary>メーターの振り分けが変わり、各レールの表示可否を見直す必要があるときに発火する。</summary>
    public event EventHandler? RailCompositionChanged;

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
            // 想定外の例外でも常駐ループを落とさない（落ちると全メーターが再起動まで止まる）
            catch (Exception)
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
            item.DetailText = snapshot.AvailabilityReason switch
            {
                TokenAvailabilityReason.AuthenticationRequired => $"設定から{snapshot.Provider}へWeb認証してください",
                TokenAvailabilityReason.AuthenticationInProgress => $"{snapshot.Provider}のWeb認証を待っています",
                TokenAvailabilityReason.NoData => $"{snapshot.Provider}公式APIに使用率データがありません",
                TokenAvailabilityReason.OfficialApiUnavailable => $"{snapshot.Provider}個人プランの公式使用率APIは未提供です",
                TokenAvailabilityReason.UnsupportedAccount => $"{snapshot.Provider}はWeb認証アカウントで接続してください",
                TokenAvailabilityReason.ServiceUnavailable => $"{snapshot.Provider}公式連携サービスを起動できません",
                TokenAvailabilityReason.NetworkError => $"{snapshot.Provider}公式APIへ接続できません · 自動再試行中",
                _ => $"{snapshot.Provider}の公式使用率を取得できません",
            };
            return;
        }

        item.SetPercent(snapshot.UsagePercent);
        var detail = string.Join(
            " · ",
            snapshot.Windows.Select(static window =>
            {
                var reset = window.ResetsAt is { } resetsAt
                    ? $" / {resetsAt.ToLocalTime():M/d HH:mm}更新"
                    : string.Empty;
                return $"{window.Label} {window.UsedPercent:0}%{reset}";
            }));
        if (!string.IsNullOrWhiteSpace(snapshot.PlanLabel))
        {
            detail += $" · {snapshot.PlanLabel}";
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
            OnPropertyChanged(nameof(IsSplitRail));
            RailCompositionChanged?.Invoke(this, EventArgs.Empty);
            LayoutSettingsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void ApplyPlacement(TaskbarPlacementService.PlacementResult placement)
    {
        IsVertical = placement.IsVertical;
        OnPropertyChanged(nameof(IsHorizontal));
        var mode = IsLayoutEditMode
            ? (IsSplitRail ? "編集モード · 左右分割中は位置固定" : "編集モード · ドラッグで移動")
            : "固定表示 · クリック透過";
        var location = placement.IsOutside ? "タスクバー外側" : "空きスペース";
        LayoutHint = _manualOffsetSaveFailed
            ? "位置を保存できませんでした · 一時配置"
            : $"{mode} · {location}";
    }

    public bool SaveManualOffset(int offsetPixels)
    {
        try
        {
            var settings = _settings.Clone();
            settings.ManualOffsetPixels = offsetPixels;
            _settingsService.Save(settings);
            _manualOffsetSaveFailed = false;
            return true;
        }
        catch (Exception ex) when (SettingsService.IsPersistenceException(ex))
        {
            _manualOffsetSaveFailed = true;
            LayoutHint = "位置を保存できませんでした · 一時配置";
            return false;
        }
    }

    private void ApplyVisibility()
    {
        LeftMetrics.Clear();
        RightMetrics.Clear();
        AddIfVisible(_cpu, _settings.ShowCpu, _settings.CpuSide);
        AddIfVisible(_memory, _settings.ShowMemory, _settings.MemorySide);
        AddIfVisible(_codex, _settings.ShowCodex, _settings.CodexSide);
        AddIfVisible(_claude, _settings.ShowClaude, _settings.ClaudeSide);
        AddIfVisible(_gemini, _settings.ShowGemini, _settings.GeminiSide);
    }

    private void AddIfVisible(MetricItemViewModel item, bool isVisible, RailSide side)
    {
        if (!isVisible)
        {
            return;
        }

        // 分割OFFのときは振り分け設定を無視し、従来位置（アイコン右の空き）へまとめる
        var target = _settings.SplitRail && side == RailSide.Left ? LeftMetrics : RightMetrics;
        target.Add(item);
    }

    private static string FormatBytes(ulong bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _cancellation.Cancel();

        var refreshTask = _refreshTask;
        _refreshTask = null;
        if (refreshTask is null || refreshTask.IsCompleted)
        {
            DisposeResources();
            return;
        }

        // 更新ループが止まってから共有サービスを破棄する（UIスレッドはブロックしない）
        _ = refreshTask.ContinueWith(
            static (_, state) => ((TaskbarViewModel)state!).DisposeResources(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeResources()
    {
        _tokenUsageService.Dispose();
        _cancellation.Dispose();
    }
}
