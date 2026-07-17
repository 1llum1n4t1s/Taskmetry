using System.Text;
using System.Text.Json;
using Taskmetry.Models;

namespace Taskmetry.Services;

public sealed class TokenUsageService
{
    private const int TailReadLimitBytes = 4 * 1024 * 1024;
    private readonly string _userProfile;

    public TokenUsageService(string? userProfile = null)
    {
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
        var file = FindNewestFile(root, static path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return TokenUsageSnapshot.Unavailable("Codex");
        }

        foreach (var line in ReadTailLines(file).Reverse())
        {
            if (TryParseCodexUsage(line, out var snapshot))
            {
                return snapshot with { UpdatedAt = File.GetLastWriteTimeUtc(file) };
            }
        }

        return TokenUsageSnapshot.Unavailable("Codex");
    }

    private TokenUsageSnapshot ReadClaude(long contextLimit)
    {
        var root = Path.Combine(_userProfile, ".claude", "projects");
        var subagentsSegment = $"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}";
        var file = FindNewestFile(root, path =>
            path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            && !path.Contains(subagentsSegment, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return TokenUsageSnapshot.Unavailable("Claude");
        }

        foreach (var line in ReadTailLines(file).Reverse())
        {
            if (TryParseClaudeUsage(line, contextLimit, out var snapshot))
            {
                return snapshot with { UpdatedAt = File.GetLastWriteTimeUtc(file) };
            }
        }

        return TokenUsageSnapshot.Unavailable("Claude");
    }

    private TokenUsageSnapshot ReadGemini(long contextLimit)
    {
        var root = Path.Combine(_userProfile, ".gemini", "tmp");
        var file = FindNewestFile(root, static path =>
            (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            && Path.GetFileName(path).StartsWith("session-", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return TokenUsageSnapshot.Unavailable("Gemini");
        }

        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= 16 * 1024 * 1024)
                {
                    var json = File.ReadAllText(file);
                    if (TryParseLegacyGeminiUsage(json, contextLimit, out var legacySnapshot))
                    {
                        return legacySnapshot with { UpdatedAt = info.LastWriteTimeUtc };
                    }
                }
            }
            catch (IOException)
            {
                return TokenUsageSnapshot.Unavailable("Gemini");
            }
            catch (UnauthorizedAccessException)
            {
                return TokenUsageSnapshot.Unavailable("Gemini");
            }
        }

        foreach (var line in ReadTailLines(file).Reverse())
        {
            if (TryParseGeminiUsage(line, contextLimit, out var snapshot))
            {
                return snapshot with { UpdatedAt = File.GetLastWriteTimeUtc(file) };
            }
        }

        return TokenUsageSnapshot.Unavailable("Gemini");
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

    private static string? FindNewestFile(string root, Func<string, bool> predicate)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            return Directory.EnumerateFiles(root, "*", options)
                .Where(predicate)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadTailLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytesToRead = (int)Math.Min(stream.Length, TailReadLimitBytes);
            if (bytesToRead <= 0)
            {
                return [];
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
                return lines[1..];
            }

            return lines;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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
}
