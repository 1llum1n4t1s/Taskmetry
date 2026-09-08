using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Taskmetry.Models;

namespace Taskmetry.Services;

internal enum ClaudeWebFailureKind
{
    AuthenticationRequired,
    ServiceUnavailable,
    NetworkError,
}

internal sealed class ClaudeWebUsageException : IOException
{
    internal ClaudeWebUsageException(
        ClaudeWebFailureKind failureKind,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        StatusCode = statusCode;
    }

    internal ClaudeWebFailureKind FailureKind { get; }

    internal HttpStatusCode? StatusCode { get; }
}

internal sealed record ClaudeWebUsageResult(
    IReadOnlyList<TokenUsageWindow> Windows,
    string? OrganizationName = null);

internal interface IClaudeWebUsageClient : IDisposable
{
    bool HasSessionKey();

    Task<ClaudeWebUsageResult> ReadUsageAsync(CancellationToken cancellationToken);

    Task<ClaudeWebUsageResult> ConnectAsync(string sessionKey, CancellationToken cancellationToken);

    void Disconnect();
}

internal sealed class ClaudeWebUsageClient : IClaudeWebUsageClient
{
    internal const string CredentialTargetName = "Taskmetry/Claude.ai/sessionKey";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly Uri ClaudeOrigin = new("https://claude.ai/");
    private static readonly Uri UsagePage = new("https://claude.ai/settings/usage");
    private readonly HttpClient _httpClient;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly bool _ownsHttpClient;
    private string? _organizationId;
    private string? _organizationName;
    private bool _disposed;

    internal ClaudeWebUsageClient()
        : this(CreateHttpClient(), new WindowsCredentialStore(), ownsHttpClient: true)
    {
    }

    internal ClaudeWebUsageClient(
        HttpClient httpClient,
        ISecureCredentialStore credentialStore,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _credentialStore = credentialStore;
        _ownsHttpClient = ownsHttpClient;
    }

    public bool HasSessionKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _credentialStore.Contains(CredentialTargetName);
    }

    public async Task<ClaudeWebUsageResult> ReadUsageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sessionKey = _credentialStore.Read(CredentialTargetName);
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ClaudeWebUsageException(
                ClaudeWebFailureKind.AuthenticationRequired,
                "ClaudeのSession Tokenが設定されていません。");
        }

        ValidateSessionKey(sessionKey);
        return await ReadUsageWithSessionKeyAsync(
            sessionKey,
            forceOrganizationRefresh: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaudeWebUsageResult> ConnectAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSessionKey(sessionKey);

        var usage = await ReadUsageWithSessionKeyAsync(
            sessionKey,
            forceOrganizationRefresh: true,
            cancellationToken).ConfigureAwait(false);
        _credentialStore.Write(CredentialTargetName, sessionKey);
        return usage;
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _credentialStore.Delete(CredentialTargetName);
        _organizationId = null;
        _organizationName = null;
    }

    internal static void ValidateSessionKey(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (!sessionKey.StartsWith("sk-ant-sid01-", StringComparison.Ordinal)
            || sessionKey.Any(static character => char.IsWhiteSpace(character)
                || char.IsControl(character)
                || character == ';')
            || Encoding.UTF8.GetByteCount(sessionKey) > 2560)
        {
            throw new ArgumentException(
                "ClaudeのSession Tokenは sk-ant-sid01- で始まる値を貼り付けてください。",
                nameof(sessionKey));
        }
    }

    internal static ClaudeWebUsageResult ParseUsage(JsonElement root, string? organizationName = null)
    {
        var windows = new List<TokenUsageWindow>();
        AddWindow(root, "five_hour", "5時間枠", 300, windows);
        AddWindow(root, "seven_day", "7日枠", 10_080, windows);
        AddWindow(root, "seven_day_opus", "7日枠 · Opus", 10_080, windows);
        AddWindow(root, "seven_day_sonnet", "7日枠 · Sonnet", 10_080, windows);
        AddWindow(root, "seven_day_oauth_apps", "7日枠 · OAuth Apps", 10_080, windows);
        AddWindow(root, "seven_day_omelette", "7日枠 · Omelette", 10_080, windows);
        AddWindow(root, "seven_day_cowork", "7日枠 · Cowork", 10_080, windows);
        AddWindow(root, "extra_usage", "追加使用量", null, windows);
        return new ClaudeWebUsageResult(windows, organizationName);
    }

    private async Task<ClaudeWebUsageResult> ReadUsageWithSessionKeyAsync(
        string sessionKey,
        bool forceOrganizationRefresh,
        CancellationToken cancellationToken)
    {
        if (forceOrganizationRefresh || string.IsNullOrWhiteSpace(_organizationId))
        {
            await DiscoverOrganizationAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ReadOrganizationUsageAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        }
        catch (ClaudeWebUsageException ex) when (
            !forceOrganizationRefresh && ex.StatusCode == HttpStatusCode.NotFound)
        {
            await DiscoverOrganizationAsync(sessionKey, cancellationToken).ConfigureAwait(false);
            return await ReadOrganizationUsageAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DiscoverOrganizationAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(
            "/api/organizations",
            sessionKey,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw ChangedResponseException();
        }

        var candidates = new List<OrganizationCandidate>();
        foreach (var organization in document.RootElement.EnumerateArray())
        {
            if (organization.ValueKind != JsonValueKind.Object
                || !TryReadString(organization, "uuid", out var id))
            {
                continue;
            }

            _ = TryReadString(organization, "name", out var name);
            var supportsClaude = organization.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Array
                && capabilities.EnumerateArray().Any(static capability =>
                    capability.ValueKind == JsonValueKind.String
                    && capability.GetString() is { } value
                    && (string.Equals(value, "chat", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "claude_max", StringComparison.OrdinalIgnoreCase)));
            candidates.Add(new OrganizationCandidate(id, name, supportsClaude));
        }

        var selected = candidates.FirstOrDefault(static candidate => candidate.SupportsClaude)
            ?? candidates.FirstOrDefault();
        if (selected is null)
        {
            throw new ClaudeWebUsageException(
                ClaudeWebFailureKind.ServiceUnavailable,
                "Claudeの利用可能な組織を確認できませんでした。");
        }

        _organizationId = selected.Id;
        _organizationName = selected.Name;
    }

    private async Task<ClaudeWebUsageResult> ReadOrganizationUsageAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_organizationId))
        {
            throw ChangedResponseException();
        }

        using var document = await SendJsonAsync(
            $"/api/organizations/{Uri.EscapeDataString(_organizationId)}/usage",
            sessionKey,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw ChangedResponseException();
        }

        return ParseUsage(document.RootElement, _organizationName);
    }

    private async Task<JsonDocument> SendJsonAsync(
        string path,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Referrer = UsagePage;
            request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _organizationId = null;
                _organizationName = null;
                throw new ClaudeWebUsageException(
                    ClaudeWebFailureKind.AuthenticationRequired,
                    "ClaudeのSession Tokenが無効または期限切れです。",
                    response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
            {
                throw new ClaudeWebUsageException(
                    ClaudeWebFailureKind.NetworkError,
                    "claude.aiが一時的に応答できません。",
                    response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeWebUsageException(
                    ClaudeWebFailureKind.ServiceUnavailable,
                    "claude.aiのWeb応答仕様を確認できません。",
                    response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw ChangedResponseException();
            }

            var payload = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException ex)
            {
                throw ChangedResponseException(ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClaudeWebUsageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new ClaudeWebUsageException(
                ClaudeWebFailureKind.NetworkError,
                "claude.aiへ接続できません。",
                innerException: ex);
        }
    }

    private static void AddWindow(
        JsonElement root,
        string propertyName,
        string label,
        int? durationMinutes,
        List<TokenUsageWindow> windows)
    {
        if (!root.TryGetProperty(propertyName, out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("utilization", out var utilization)
            || utilization.ValueKind != JsonValueKind.Number
            || !utilization.TryGetDouble(out var usedPercent))
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (source.TryGetProperty("resets_at", out var resets)
            && resets.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(resets.GetString(), out var parsedReset))
        {
            resetsAt = parsedReset;
        }

        windows.Add(new TokenUsageWindow(
            label,
            Math.Clamp(usedPercent, 0, 100),
            durationMinutes,
            resetsAt));
    }

    private static bool TryReadString(JsonElement source, string propertyName, out string value)
    {
        value = string.Empty;
        if (!source.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static ClaudeWebUsageException ChangedResponseException(Exception? innerException = null)
        => new(
            ClaudeWebFailureKind.ServiceUnavailable,
            "claude.aiのWeb応答仕様が変更された可能性があります。",
            innerException: innerException);

    /// <summary>
    /// Content-Length が無い応答でも上限を超えた時点で読み取りを打ち切る。
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw ChangedResponseException();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = ClaudeOrigin,
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Taskmetry/1.0");
        return client;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record OrganizationCandidate(string Id, string Name, bool SupportsClaude);
}

internal sealed class UnconfiguredClaudeWebUsageClient : IClaudeWebUsageClient
{
    public bool HasSessionKey() => false;

    public Task<ClaudeWebUsageResult> ReadUsageAsync(CancellationToken cancellationToken)
        => throw new ClaudeWebUsageException(
            ClaudeWebFailureKind.AuthenticationRequired,
            "ClaudeのSession Tokenが設定されていません。");

    public Task<ClaudeWebUsageResult> ConnectAsync(string sessionKey, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public void Disconnect()
    {
    }

    public void Dispose()
    {
    }
}
