namespace Taskmetry.Models;

public enum RailPlacementMode
{
    Auto,
    InsideTaskbar,
    OutsideTaskbar,
}

public sealed class AppSettings
{
    public bool FirstRun { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ShowGemini { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool LayoutEditMode { get; set; }
    public RailPlacementMode PlacementMode { get; set; } = RailPlacementMode.Auto;
    public int ManualOffsetPixels { get; set; }
    public int PreferredWidthPixels { get; set; } = 740;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public long ClaudeContextLimit { get; set; } = 1_000_000;
    public long GeminiContextLimit { get; set; } = 1_048_576;

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
        ClaudeContextLimit = Math.Clamp(ClaudeContextLimit, 32_000, 4_000_000);
        GeminiContextLimit = Math.Clamp(GeminiContextLimit, 32_000, 4_000_000);
    }
}
