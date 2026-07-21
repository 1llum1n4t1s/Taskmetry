using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class TokenUsageServiceTests
{
    [Fact]
    public void Codexの最新コンテキストと利用枠を解析できる()
    {
        const string json = """
            {
              "type":"event_msg",
              "payload":{
                "type":"token_count",
                "info":{
                  "last_token_usage":{"total_tokens":96866},
                  "model_context_window":258400
                },
                "rate_limits":{"primary":{"used_percent":55.0,"window_minutes":10080}}
              }
            }
            """;

        var parsed = TokenUsageService.TryParseCodexUsage(json, out var result);

        Assert.True(parsed);
        Assert.Equal(96_866, result.UsedTokens);
        Assert.Equal(258_400, result.ContextLimit);
        Assert.Equal(55, result.RateLimitPercent);
        Assert.Equal(10_080, result.RateLimitWindowMinutes);
    }

    [Fact]
    public void Claudeのキャッシュを含むコンテキスト量を合計できる()
    {
        const string json = """
            {
              "type":"assistant",
              "message":{
                "model":"claude-sonnet-5",
                "usage":{
                  "input_tokens":2,
                  "cache_creation_input_tokens":1701,
                  "cache_read_input_tokens":273077,
                  "output_tokens":258
                }
              }
            }
            """;

        var parsed = TokenUsageService.TryParseClaudeUsage(json, 1_000_000, out var result);

        Assert.True(parsed);
        Assert.Equal(275_038, result.UsedTokens);
        Assert.Equal("claude-sonnet-5", result.Model);
        Assert.Equal(27.5038, result.ContextPercent, precision: 4);
    }

    [Fact]
    public void Geminiの記録済みtokenSummaryを解析できる()
    {
        const string json = """
            {
              "id":"message-1",
              "type":"gemini",
              "model":"gemini-3-pro",
              "tokens":{"input":12000,"output":500,"cached":3000,"thoughts":200,"tool":40,"total":15740}
            }
            """;

        var parsed = TokenUsageService.TryParseGeminiUsage(json, 1_048_576, out var result);

        Assert.True(parsed);
        Assert.Equal(15_740, result.UsedTokens);
        Assert.Equal("gemini-3-pro", result.Model);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}")]
    public void Codexの破損または必須項目不足を安全に拒否する(string json)
    {
        Assert.False(TokenUsageService.TryParseCodexUsage(json, out _));
    }

    [Fact]
    public void Claudeのtoken合計がゼロなら利用可能として扱わない()
    {
        const string json = """
            {"type":"assistant","message":{"usage":{"input_tokens":0,"output_tokens":0}}}
            """;

        Assert.False(TokenUsageService.TryParseClaudeUsage(json, 1_000_000, out _));
    }

    [Fact]
    public void Geminiの旧形式では末尾側の最新有効メッセージを選ぶ()
    {
        const string json = """
            {
              "messages": [
                {"type":"gemini","model":"old","tokens":{"total":100}},
                {"type":"user","content":"本文は解析対象外"},
                {"type":"gemini","model":"latest","tokens":{"total":300}}
              ]
            }
            """;

        var parsed = TokenUsageService.TryParseLegacyGeminiUsage(json, 1_048_576, out var result);

        Assert.True(parsed);
        Assert.Equal(300, result.UsedTokens);
        Assert.Equal("latest", result.Model);
    }

    [Fact]
    public async Task 未変更ログは解析結果を再利用し追記後だけ更新する()
    {
        var profile = CreateTemporaryProfile();
        try
        {
            var sessions = Path.Combine(profile, ".codex", "sessions");
            Directory.CreateDirectory(sessions);
            var file = Path.Combine(sessions, "session.jsonl");
            await File.WriteAllTextAsync(file, CreateCodexUsageLine(100), TestContext.Current.CancellationToken);

            using var service = new TokenUsageService(profile);
            var settings = new Taskmetry.Models.AppSettings();
            var first = await service.ReadAllAsync(settings, TestContext.Current.CancellationToken);
            var second = await service.ReadAllAsync(settings, TestContext.Current.CancellationToken);

            Assert.Same(first["Codex"], second["Codex"]);

            await File.AppendAllTextAsync(
                file,
                Environment.NewLine + CreateCodexUsageLine(200),
                TestContext.Current.CancellationToken);
            var third = await service.ReadAllAsync(settings, TestContext.Current.CancellationToken);

            Assert.NotSame(second["Codex"], third["Codex"]);
            Assert.Equal(200, third["Codex"].UsedTokens);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public async Task 記録が存在するが形式不明なら未使用と区別する()
    {
        var profile = CreateTemporaryProfile();
        try
        {
            var sessions = Path.Combine(profile, ".codex", "sessions");
            Directory.CreateDirectory(sessions);
            await File.WriteAllTextAsync(
                Path.Combine(sessions, "session.jsonl"),
                "{\"type\":\"future_format\"}",
                TestContext.Current.CancellationToken);

            using var service = new TokenUsageService(profile);
            var result = await service.ReadAllAsync(
                new Taskmetry.Models.AppSettings(),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                Taskmetry.Models.TokenAvailabilityReason.UnsupportedFormat,
                result["Codex"].AvailabilityReason);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public async Task 記録がない状態を再読込しても未検出理由を維持する()
    {
        var profile = CreateTemporaryProfile();
        try
        {
            using var service = new TokenUsageService(profile);

            var first = await service.ReadAllAsync(
                new Taskmetry.Models.AppSettings(),
                TestContext.Current.CancellationToken);
            var second = await service.ReadAllAsync(
                new Taskmetry.Models.AppSettings(),
                TestContext.Current.CancellationToken);

            Assert.Equal(Taskmetry.Models.TokenAvailabilityReason.NotFound, first["Codex"].AvailabilityReason);
            Assert.Equal(Taskmetry.Models.TokenAvailabilityReason.NotFound, second["Codex"].AvailabilityReason);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    private static string CreateTemporaryProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Taskmetry.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateCodexUsageLine(long usedTokens) => """
        {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"total_tokens":__USED_TOKENS__},"model_context_window":258400}}}
        """.Replace("__USED_TOKENS__", usedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
