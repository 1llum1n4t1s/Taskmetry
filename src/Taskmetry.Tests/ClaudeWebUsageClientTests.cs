using System.Net;
using System.Text;
using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class ClaudeWebUsageClientTests
{
    [Fact]
    public async Task Claude公式Web応答から利用枠を解析して接続成功後だけ保存する()
    {
        const string sessionKey = "sk-ant-sid01-test-session-token";
        var requests = new List<(string Path, string? Cookie)>();
        var handler = new QueueHttpMessageHandler(
            Json(HttpStatusCode.OK, """
                [
                  {
                    "uuid": "org-1",
                    "name": "Personal",
                    "capabilities": ["chat", "claude_max"]
                  }
                ]
                """),
            Json(HttpStatusCode.OK, """
                {
                  "five_hour": {
                    "utilization": 27.5,
                    "resets_at": "2026-07-22T05:00:00Z"
                  },
                  "seven_day": {
                    "utilization": 61,
                    "resets_at": "2026-07-27T00:00:00Z"
                  },
                  "seven_day_sonnet": {
                    "utilization": 42,
                    "resets_at": "2026-07-27T00:00:00Z"
                  }
                }
                """));
        handler.RequestObserved += request => requests.Add((
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://claude.ai/"),
        };
        var credentials = new FakeCredentialStore();
        using var client = new ClaudeWebUsageClient(httpClient, credentials);

        var result = await client.ConnectAsync(sessionKey, TestContext.Current.CancellationToken);

        Assert.Equal("Personal", result.OrganizationName);
        Assert.Collection(
            result.Windows,
            window =>
            {
                Assert.Equal("5時間枠", window.Label);
                Assert.Equal(27.5, window.UsedPercent);
                Assert.Equal(300, window.WindowDurationMinutes);
                Assert.NotNull(window.ResetsAt);
            },
            window =>
            {
                Assert.Equal("7日枠", window.Label);
                Assert.Equal(61, window.UsedPercent);
            },
            window =>
            {
                Assert.Equal("7日枠 · Sonnet", window.Label);
                Assert.Equal(42, window.UsedPercent);
            });
        Assert.Equal(sessionKey, credentials.Secret);
        Assert.Equal(
            ["/api/organizations", "/api/organizations/org-1/usage"],
            requests.Select(static request => request.Path));
        Assert.All(requests, request => Assert.Equal($"sessionKey={sessionKey}", request.Cookie));
    }

    [Fact]
    public async Task 無効なSessionTokenは通信も保存もしない()
    {
        var handler = new QueueHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://claude.ai/"),
        };
        var credentials = new FakeCredentialStore();
        using var client = new ClaudeWebUsageClient(httpClient, credentials);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync(
            "invalid-token",
            TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.SendCount);
        Assert.Null(credentials.Secret);
    }

    [Fact]
    public async Task 認証拒否されたSessionTokenは保存しない()
    {
        var handler = new QueueHttpMessageHandler(Json(HttpStatusCode.Unauthorized, "{}"));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://claude.ai/"),
        };
        var credentials = new FakeCredentialStore();
        using var client = new ClaudeWebUsageClient(httpClient, credentials);

        var exception = await Assert.ThrowsAsync<ClaudeWebUsageException>(() => client.ConnectAsync(
            "sk-ant-sid01-expired-session-token",
            TestContext.Current.CancellationToken));

        Assert.Equal(ClaudeWebFailureKind.AuthenticationRequired, exception.FailureKind);
        Assert.Null(credentials.Secret);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeCredentialStore : ISecureCredentialStore
    {
        internal string? Secret { get; private set; }

        public bool Contains(string targetName) => Secret is not null;

        public string? Read(string targetName) => Secret;

        public void Write(string targetName, string secret) => Secret = secret;

        public void Delete(string targetName) => Secret = null;
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        internal event Action<HttpRequestMessage>? RequestObserved;

        internal int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            RequestObserved?.Invoke(request);
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("Unexpected HTTP request.");
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
