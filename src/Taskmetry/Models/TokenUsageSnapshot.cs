namespace Taskmetry.Models;

public enum TokenAvailabilityReason
{
    None,
    AuthenticationRequired,
    AuthenticationInProgress,
    NoData,
    OfficialApiUnavailable,
    UnsupportedAccount,
    ServiceUnavailable,
    NetworkError,
}

public sealed record TokenUsageWindow(
    string Label,
    double UsedPercent,
    int? WindowDurationMinutes = null,
    DateTimeOffset? ResetsAt = null);

public sealed record TokenUsageSnapshot(
    string Provider,
    IReadOnlyList<TokenUsageWindow> Windows,
    string? AccountLabel = null,
    string? PlanLabel = null,
    DateTimeOffset? UpdatedAt = null,
    TokenAvailabilityReason AvailabilityReason = TokenAvailabilityReason.None)
{
    public bool IsAvailable => Windows.Count > 0;

    public TokenUsageWindow? MostUsedWindow => Windows.Count == 0
        ? null
        : Windows.MaxBy(static window => window.UsedPercent);

    public double UsagePercent => MostUsedWindow?.UsedPercent ?? 0;

    public static TokenUsageSnapshot Unavailable(
        string provider,
        TokenAvailabilityReason reason)
        => new(provider, [], AvailabilityReason: reason);
}

public enum LlmConnectionStatus
{
    Connected,
    AuthenticationRequired,
    AuthenticationInProgress,
    OfficialApiUnavailable,
    UnsupportedAccount,
    ServiceUnavailable,
    NetworkError,
}

public sealed record LlmConnectionSnapshot(
    string Provider,
    LlmConnectionStatus Status,
    string Description,
    string? AccountLabel = null)
{
    public bool IsConnected => Status == LlmConnectionStatus.Connected;
}
