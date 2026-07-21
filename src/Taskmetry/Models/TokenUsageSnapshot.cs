namespace Taskmetry.Models;

public enum TokenAvailabilityReason
{
    None,
    NotFound,
    NoData,
    AccessDenied,
    IoError,
    UnsupportedFormat,
    TooLarge,
}

public sealed record TokenUsageSnapshot(
    string Provider,
    long UsedTokens,
    long ContextLimit,
    string? Model = null,
    double? RateLimitPercent = null,
    int? RateLimitWindowMinutes = null,
    DateTimeOffset? UpdatedAt = null,
    TokenAvailabilityReason AvailabilityReason = TokenAvailabilityReason.None)
{
    public bool IsAvailable => UsedTokens > 0 && ContextLimit > 0;
    public double ContextPercent => ContextLimit <= 0 ? 0 : Math.Clamp(UsedTokens * 100d / ContextLimit, 0, 100);

    public static TokenUsageSnapshot Unavailable(
        string provider,
        TokenAvailabilityReason reason = TokenAvailabilityReason.NotFound)
        => new(provider, 0, 0, AvailabilityReason: reason);
}
