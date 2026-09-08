namespace Taskmetry.Models;

public enum RailPlacementMode
{
    Auto,
    InsideTaskbar,
    OutsideTaskbar,
}

/// <summary>
/// メーターを配置するレール。縦置きタスクバーでは Left が上、Right が下になる。
/// </summary>
public enum RailSide
{
    Left,
    Right,
}

public sealed class AppSettings
{
    public bool FirstRun { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ShowGemini { get; set; } = true;
    public bool SplitRail { get; set; }
    public RailSide CpuSide { get; set; } = RailSide.Left;
    public RailSide MemorySide { get; set; } = RailSide.Left;
    public RailSide CodexSide { get; set; } = RailSide.Right;
    public RailSide ClaudeSide { get; set; } = RailSide.Right;
    public RailSide GeminiSide { get; set; } = RailSide.Right;
    public bool StartWithWindows { get; set; }
    public bool LayoutEditMode { get; set; }
    public RailPlacementMode PlacementMode { get; set; } = RailPlacementMode.Auto;
    public int ManualOffsetPixels { get; set; }
    public int PreferredWidthPixels { get; set; } = 740;
    public int RefreshIntervalSeconds { get; set; } = 2;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Sanitize()
    {
        PreferredWidthPixels = Math.Clamp(PreferredWidthPixels, 420, 1_200);
        if (!Enum.IsDefined(PlacementMode))
        {
            PlacementMode = RailPlacementMode.Auto;
        }

        ManualOffsetPixels = Math.Clamp(ManualOffsetPixels, -10_000, 10_000);
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 1, 30);
        CpuSide = Normalize(CpuSide);
        MemorySide = Normalize(MemorySide);
        CodexSide = Normalize(CodexSide);
        ClaudeSide = Normalize(ClaudeSide);
        GeminiSide = Normalize(GeminiSide);
    }

    private static RailSide Normalize(RailSide side)
        => Enum.IsDefined(side) ? side : RailSide.Left;
}
