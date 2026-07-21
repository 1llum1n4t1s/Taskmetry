using System.Text;
using System.Text.Json;
using Taskmetry.Models;

namespace Taskmetry.Services;

public sealed class TokenUsageService : IDisposable
{
    private const int TailReadLimitBytes = 4 * 1024 * 1024;
    private const int LegacyJsonReadLimitBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(1);
    private readonly string _userProfile;
    private readonly TimeProvider _timeProvider;
    private readonly ProviderCache _codexCache = new();
    private readonly ProviderCache _claudeCache = new();
    private readonly ProviderCache _geminiCache = new();
    private bool _disposed;

    public TokenUsageService(string? userProfile = null, TimeProvider? timeProvider = null)
    {
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyDictionary<string, TokenUsageSnapshot>> ReadAllAsync(AppSettings settings, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyDictionary<string, TokenUsageSnapshot>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = new Dictionary<string, TokenUsageSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Codex"] = ReadCodex(),
                ["Claude"] = ReadClaude(settings.ClaudeContextLimit),
                ["Gemini"] = ReadGemini(settings.GeminiContextLimit),
            };
            return snapshots;
        }, cancellationToken);

    private TokenUsageSnapshot ReadCodex()
    {
        var root = Path.Combine(_userProfile, ".codex", "sessions");
        var search = FindNewestFile(
            root,
            static path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase),
            _codexCache);
        if (search.Path is null)
        {
            return TokenUsageSnapshot.Unavailable("Codex", search.FailureReason);
        }

        return ReadJsonLines(
            _codexCache,
            "Codex",
            search.Path,
            contextLimit: 0,
            static (string json, long _, out TokenUsageSnapshot snapshot) => TryParseCodexUsage(json, out snapshot));
    }

    private TokenUsageSnapshot ReadClaude(long contextLimit)
    {
        var root = Path.Combine(_userProfile, ".claude", "projects");
        var subagentsSegment = $"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}";
        var search = FindNewestFile(
            root,
            path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(subagentsSegment, StringComparison.OrdinalIgnoreCase),
            _claudeCache);
        if (search.Path is null)
        {
            return TokenUsageSnapshot.Unavailable("Claude", search.FailureReason);
        }

        return ReadJsonLines(
            _claudeCache,
            "Claude",
            search.Path,
            contextLimit,
            static (string json, long limit, out TokenUsageSnapshot snapshot) => TryParseClaudeUsage(json, limit, out snapshot));
    }

    private TokenUsageSnapshot ReadGemini(long contextLimit)
    {
        var root = Path.Combine(_userProfile, ".gemini", "tmp");
        var search = FindNewestFile(
            root,
            static path => (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                && Path.GetFileName(path).StartsWith("session-", StringComparison.OrdinalIgnoreCase),
            _geminiCache);
        if (search.Path is null)
        {
            return TokenUsageSnapshot.Unavailable("Gemini", search.FailureReason);
        }

        if (search.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return ReadLegacyGemini(_geminiCache, search.Path, contextLimit);
        }

        return ReadJsonLines(
            _geminiCache,
            "Gemini",
            search.Path,
            contextLimit,
            static (string json, long limit, out TokenUsageSnapshot snapshot) => TryParseGeminiUsage(json, limit, out snapshot));
    }

    private TokenUsageSnapshot ReadJsonLines(
        ProviderCache cache,
        string provider,
        string path,
        long contextLimit,
        UsageParser parser)
    {
        if (!TryGetFileStamp(path, out var stamp, out var failureReason))
        {
            return StoreCached(cache, default, contextLimit, TokenUsageSnapshot.Unavailable(provider, failureReason));
        }

        if (TryGetCached(cache, stamp, contextLimit, out var cached))
        {
            return cached;
        }

        Interlocked.Exchange(ref cache.ContentDirty, 0);
        var tail = ReadTailLines(path);
        if (tail.FailureReason != TokenAvailabilityReason.None)
        {
            return StoreCached(cache, stamp, contextLimit, TokenUsageSnapshot.Unavailable(provider, tail.FailureReason));
        }

        foreach (var line in tail.Lines.Reverse())
        {
            if (parser(line, contextLimit, out var snapshot))
            {
                return StoreCached(cache, stamp, contextLimit, snapshot with { UpdatedAt = stamp.LastWriteTimeUtc });
            }
        }

        var reason = tail.Lines.Count == 0
            ? TokenAvailabilityReason.NoData
            : TokenAvailabilityReason.UnsupportedFormat;
        return StoreCached(cache, stamp, contextLimit, TokenUsageSnapshot.Unavailable(provider, reason));
    }

    private TokenUsageSnapshot ReadLegacyGemini(ProviderCache cache, string path, long contextLimit)
    {
        if (!TryGetFileStamp(path, out var stamp, out var failureReason))
        {
            return StoreCached(cache, default, contextLimit, TokenUsageSnapshot.Unavailable("Gemini", failureReason));
        }

        if (TryGetCached(cache, stamp, contextLimit, out var cached))
        {
            return cached;
        }

        Interlocked.Exchange(ref cache.ContentDirty, 0);
        if (stamp.Length > LegacyJsonReadLimitBytes)
        {
            return StoreCached(
                cache,
                stamp,
                contextLimit,
                TokenUsageSnapshot.Unavailable("Gemini", TokenAvailabilityReason.TooLarge));
        }

        try
        {
            var json = File.ReadAllText(path);
            var snapshot = TryParseLegacyGeminiUsage(json, contextLimit, out var parsed)
                ? parsed with { UpdatedAt = stamp.LastWriteTimeUtc }
                : TokenUsageSnapshot.Unavailable("Gemini", json.Length == 0
                    ? TokenAvailabilityReason.NoData
                    : TokenAvailabilityReason.UnsupportedFormat);
            return StoreCached(cache, stamp, contextLimit, snapshot);
        }
        catch (UnauthorizedAccessException)
        {
            return StoreCached(
                cache,
                stamp,
                contextLimit,
                TokenUsageSnapshot.Unavailable("Gemini", TokenAvailabilityReason.AccessDenied));
        }
        catch (IOException)
        {
            return StoreCached(
                cache,
                stamp,
                contextLimit,
                TokenUsageSnapshot.Unavailable("Gemini", TokenAvailabilityReason.IoError));
        }
        catch (System.Security.SecurityException)
        {
            return StoreCached(
                cache,
                stamp,
                contextLimit,
                TokenUsageSnapshot.Unavailable("Gemini", TokenAvailabilityReason.AccessDenied));
        }
    }

    internal static bool TryParseCodexUsage(string json, out TokenUsageSnapshot snapshot)
    {
        snapshot = TokenUsageSnapshot.Unavailable("Codex");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var type) || type != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || !TryGetString(payload, "type", out var payloadType) || payloadType != "token_count"
                || !payload.TryGetProperty("info", out var info)
                || !info.TryGetProperty("last_token_usage", out var lastUsage)
                || !TryGetInt64(lastUsage, "total_tokens", out var used)
                || !TryGetInt64(info, "model_context_window", out var limit))
            {
                return false;
            }

            double? rateLimit = null;
            int? windowMinutes = null;
            if (payload.TryGetProperty("rate_limits", out var rateLimits)
                && rateLimits.TryGetProperty("primary", out var primary))
            {
                if (primary.TryGetProperty("used_percent", out var usedPercent) && usedPercent.TryGetDouble(out var percent))
                {
                    rateLimit = percent;
                }

                if (TryGetInt64(primary, "window_minutes", out var minutes) && minutes <= int.MaxValue)
                {
                    windowMinutes = (int)minutes;
                }
            }

            snapshot = new TokenUsageSnapshot("Codex", used, limit, RateLimitPercent: rateLimit, RateLimitWindowMinutes: windowMinutes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseClaudeUsage(string json, long contextLimit, out TokenUsageSnapshot snapshot)
    {
        snapshot = TokenUsageSnapshot.Unavailable("Claude");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetString(root, "type", out var type) || type != "assistant"
                || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("usage", out var usage))
            {
                return false;
            }

            var used = SumTokenFields(usage,
                "input_tokens",
                "cache_creation_input_tokens",
                "cache_read_input_tokens",
                "output_tokens");
            if (used <= 0)
            {
                return false;
            }

            _ = TryGetString(message, "model", out var model);
            snapshot = new TokenUsageSnapshot("Claude", used, contextLimit, model);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseGeminiUsage(string json, long contextLimit, out TokenUsageSnapshot snapshot)
    {
        snapshot = TokenUsageSnapshot.Unavailable("Gemini");
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParseGeminiMessage(document.RootElement, contextLimit, out snapshot);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseLegacyGeminiUsage(string json, long contextLimit, out TokenUsageSnapshot snapshot)
    {
        snapshot = TokenUsageSnapshot.Unavailable("Gemini");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            for (var index = messages.GetArrayLength() - 1; index >= 0; index--)
            {
                if (TryParseGeminiMessage(messages[index], contextLimit, out snapshot))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseGeminiMessage(JsonElement message, long contextLimit, out TokenUsageSnapshot snapshot)
    {
        snapshot = TokenUsageSnapshot.Unavailable("Gemini");
        if (!TryGetString(message, "type", out var type) || type != "gemini"
            || !message.TryGetProperty("tokens", out var tokens))
        {
            return false;
        }

        long used;
        if (!TryGetInt64(tokens, "total", out used))
        {
            used = SumTokenFields(tokens, "input", "output", "cached", "thoughts", "tool");
        }

        if (used <= 0)
        {
            return false;
        }

        _ = TryGetString(message, "model", out var model);
        snapshot = new TokenUsageSnapshot("Gemini", used, contextLimit, model);
        return true;
    }

    private FileSearchResult FindNewestFile(
        string root,
        Func<string, bool> predicate,
        ProviderCache cache)
    {
        EnsureWatcher(root, cache);
        var now = _timeProvider.GetUtcNow();
        string? cachedPath;
        DateTimeOffset nextFullScanUtc;
        bool hasScanned;
        TokenAvailabilityReason latestSearchReason;
        lock (cache.Gate)
        {
            cachedPath = cache.LatestPath;
            nextFullScanUtc = cache.NextFullScanUtc;
            hasScanned = cache.HasScanned;
            latestSearchReason = cache.LatestSearchReason;
        }

        var requiresScan = Volatile.Read(ref cache.IndexDirty) != 0
            || !hasScanned
            || now >= nextFullScanUtc
            || cachedPath is not null && !File.Exists(cachedPath);
        if (!requiresScan)
        {
            return new FileSearchResult(cachedPath, latestSearchReason);
        }

        Interlocked.Exchange(ref cache.IndexDirty, 0);
        if (!Directory.Exists(root))
        {
            UpdateLatestPath(
                cache,
                null,
                TokenAvailabilityReason.NotFound,
                now + TimeSpan.FromSeconds(5));
            return new FileSearchResult(null, TokenAvailabilityReason.NotFound);
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            string? newestPath = null;
            var newestWriteTime = DateTime.MinValue;
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                if (!predicate(path))
                {
                    continue;
                }

                DateTime writeTime;
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(path);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                if (newestPath is null
                    || writeTime > newestWriteTime
                    || writeTime == newestWriteTime
                        && StringComparer.OrdinalIgnoreCase.Compare(path, newestPath) > 0)
                {
                    newestPath = path;
                    newestWriteTime = writeTime;
                }
            }

            var failureReason = newestPath is null
                ? TokenAvailabilityReason.NotFound
                : TokenAvailabilityReason.None;
            UpdateLatestPath(cache, newestPath, failureReason, now + FullScanInterval);
            return new FileSearchResult(
                newestPath,
                failureReason);
        }
        catch (IOException)
        {
            UpdateNextScan(cache, TokenAvailabilityReason.IoError, now + TimeSpan.FromSeconds(5));
            return new FileSearchResult(null, TokenAvailabilityReason.IoError);
        }
        catch (UnauthorizedAccessException)
        {
            UpdateNextScan(cache, TokenAvailabilityReason.AccessDenied, now + TimeSpan.FromSeconds(5));
            return new FileSearchResult(null, TokenAvailabilityReason.AccessDenied);
        }
        catch (System.Security.SecurityException)
        {
            UpdateNextScan(cache, TokenAvailabilityReason.AccessDenied, now + TimeSpan.FromSeconds(5));
            return new FileSearchResult(null, TokenAvailabilityReason.AccessDenied);
        }
    }

    private static TailReadResult ReadTailLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytesToRead = (int)Math.Min(stream.Length, TailReadLimitBytes);
            if (bytesToRead <= 0)
            {
                return new TailReadResult([], TokenAvailabilityReason.None);
            }

            stream.Seek(-bytesToRead, SeekOrigin.End);
            var buffer = new byte[bytesToRead];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stream.Length > bytesToRead && lines.Length > 0)
            {
                return new TailReadResult(lines[1..], TokenAvailabilityReason.None);
            }

            return new TailReadResult(lines, TokenAvailabilityReason.None);
        }
        catch (IOException)
        {
            return new TailReadResult([], TokenAvailabilityReason.IoError);
        }
        catch (UnauthorizedAccessException)
        {
            return new TailReadResult([], TokenAvailabilityReason.AccessDenied);
        }
        catch (System.Security.SecurityException)
        {
            return new TailReadResult([], TokenAvailabilityReason.AccessDenied);
        }
    }

    private void EnsureWatcher(string root, ProviderCache cache)
    {
        lock (cache.Gate)
        {
            if (cache.Watcher is not null || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                };
                watcher.Created += (_, _) => MarkIndexDirty(cache);
                watcher.Deleted += (_, _) => MarkIndexDirty(cache);
                watcher.Renamed += (_, _) => MarkIndexDirty(cache);
                watcher.Changed += (_, args) => OnWatchedPathChanged(cache, args.FullPath);
                watcher.Error += (_, _) => MarkIndexDirty(cache);
                watcher.EnableRaisingEvents = true;
                cache.Watcher = watcher;
            }
            catch (IOException)
            {
                // 通知を利用できない場合も定期的な全走査で追従する。
            }
            catch (UnauthorizedAccessException)
            {
                // アクセスが回復した後の定期走査で再試行する。
            }
            catch (System.Security.SecurityException)
            {
                // 権限が回復した後の定期走査で再試行する。
            }
        }
    }

    private static void OnWatchedPathChanged(ProviderCache cache, string path)
    {
        string? latestPath;
        lock (cache.Gate)
        {
            latestPath = cache.LatestPath;
        }

        if (latestPath is not null && string.Equals(path, latestPath, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref cache.ContentDirty, 1);
            return;
        }

        MarkIndexDirty(cache);
    }

    private static void MarkIndexDirty(ProviderCache cache)
    {
        Interlocked.Exchange(ref cache.IndexDirty, 1);
        Interlocked.Exchange(ref cache.ContentDirty, 1);
    }

    private static void UpdateLatestPath(
        ProviderCache cache,
        string? path,
        TokenAvailabilityReason searchReason,
        DateTimeOffset nextFullScanUtc)
    {
        lock (cache.Gate)
        {
            if (!string.Equals(cache.LatestPath, path, StringComparison.OrdinalIgnoreCase))
            {
                cache.LatestPath = path;
                cache.ContentStamp = null;
                cache.Snapshot = null;
                Interlocked.Exchange(ref cache.ContentDirty, 1);
            }

            cache.NextFullScanUtc = nextFullScanUtc;
            cache.LatestSearchReason = searchReason;
            cache.HasScanned = true;
        }
    }

    private static void UpdateNextScan(
        ProviderCache cache,
        TokenAvailabilityReason searchReason,
        DateTimeOffset nextFullScanUtc)
    {
        lock (cache.Gate)
        {
            cache.NextFullScanUtc = nextFullScanUtc;
            cache.LatestSearchReason = searchReason;
            cache.HasScanned = true;
        }
    }

    private static bool TryGetFileStamp(
        string path,
        out FileStamp stamp,
        out TokenAvailabilityReason failureReason)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists)
            {
                stamp = default;
                failureReason = TokenAvailabilityReason.NotFound;
                return false;
            }

            stamp = new FileStamp(path, info.Length, info.LastWriteTimeUtc);
            failureReason = TokenAvailabilityReason.None;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            stamp = default;
            failureReason = TokenAvailabilityReason.AccessDenied;
            return false;
        }
        catch (IOException)
        {
            stamp = default;
            failureReason = TokenAvailabilityReason.IoError;
            return false;
        }
        catch (System.Security.SecurityException)
        {
            stamp = default;
            failureReason = TokenAvailabilityReason.AccessDenied;
            return false;
        }
    }

    private static bool TryGetCached(
        ProviderCache cache,
        FileStamp stamp,
        long contextLimit,
        out TokenUsageSnapshot snapshot)
    {
        lock (cache.Gate)
        {
            if (Volatile.Read(ref cache.ContentDirty) == 0
                && cache.ContentStamp == stamp
                && cache.ContextLimit == contextLimit
                && cache.Snapshot is { } cached)
            {
                snapshot = cached;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private static TokenUsageSnapshot StoreCached(
        ProviderCache cache,
        FileStamp stamp,
        long contextLimit,
        TokenUsageSnapshot snapshot)
    {
        lock (cache.Gate)
        {
            cache.ContentStamp = stamp.Path is null ? null : stamp;
            cache.ContextLimit = contextLimit;
            cache.Snapshot = snapshot;
        }

        return snapshot;
    }

    private static long SumTokenFields(JsonElement usage, params string[] names)
    {
        long total = 0;
        foreach (var name in names)
        {
            if (TryGetInt64(usage, name, out var value))
            {
                total += value;
            }
        }

        return total;
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeWatcher(_codexCache);
        DisposeWatcher(_claudeCache);
        DisposeWatcher(_geminiCache);
    }

    private static void DisposeWatcher(ProviderCache cache)
    {
        lock (cache.Gate)
        {
            cache.Watcher?.Dispose();
            cache.Watcher = null;
        }
    }

    private delegate bool UsageParser(string json, long contextLimit, out TokenUsageSnapshot snapshot);

    private sealed class ProviderCache
    {
        internal object Gate { get; } = new();
        internal FileSystemWatcher? Watcher { get; set; }
        internal string? LatestPath { get; set; }
        internal DateTimeOffset NextFullScanUtc { get; set; }
        internal bool HasScanned { get; set; }
        internal TokenAvailabilityReason LatestSearchReason { get; set; } = TokenAvailabilityReason.NotFound;
        internal int IndexDirty = 1;
        internal int ContentDirty = 1;
        internal FileStamp? ContentStamp { get; set; }
        internal long ContextLimit { get; set; }
        internal TokenUsageSnapshot? Snapshot { get; set; }
    }

    private readonly record struct FileSearchResult(string? Path, TokenAvailabilityReason FailureReason);
    private readonly record struct FileStamp(string? Path, long Length, DateTime LastWriteTimeUtc);
    private readonly record struct TailReadResult(
        IReadOnlyList<string> Lines,
        TokenAvailabilityReason FailureReason);
}
