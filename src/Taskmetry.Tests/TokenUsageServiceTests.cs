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
}
