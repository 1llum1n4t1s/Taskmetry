using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Taskmetry.Models;

namespace Taskmetry.Services;

internal sealed record CodexAccountInfo(
    bool IsAuthenticated,
    string? AccountType = null,
    string? Email = null,
    string? PlanType = null);

internal sealed record CodexRateLimits(IReadOnlyList<TokenUsageWindow> Windows);

internal sealed record CodexLoginStart(string LoginId, Uri AuthenticationUri);

internal interface ICodexAppServerClient : IDisposable
{
    Task<CodexAccountInfo> ReadAccountAsync(CancellationToken cancellationToken);
    Task<CodexRateLimits> ReadRateLimitsAsync(CancellationToken cancellationToken);
    Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken);
    Task<bool> WaitForLoginCompletionAsync(string loginId, CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
}

internal sealed class CodexAppServerClient : ICodexAppServerClient
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly string _executableName;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _loginCompletions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Process? _process;
    private StreamWriter? _standardInput;
    private Task? _outputReadTask;
    private Task? _errorReadTask;
    private long _nextRequestId;
    private bool _initialized;
    private bool _disposed;

    internal CodexAppServerClient(string? executableName = null)
    {
        _executableName = executableName ?? ResolveCodexExecutable();
    }

    public async Task<CodexAccountInfo> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync(
            "account/read",
            new JsonObject { ["refreshToken"] = false },
            cancellationToken).ConfigureAwait(false);
        return ParseAccount(result);
    }

    public async Task<CodexRateLimits> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("account/rateLimits/read", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return ParseRateLimits(result);
    }

    public async Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync(
            "account/login/start",
            new JsonObject
            {
                ["type"] = "chatgpt",
                ["useHostedLoginSuccessPage"] = true,
                ["appBrand"] = "chatgpt",
            },
            cancellationToken).ConfigureAwait(false);

        if (!TryGetString(result, "loginId", out var loginId)
            || !TryGetString(result, "authUrl", out var authUrl)
            || !Uri.TryCreate(authUrl, UriKind.Absolute, out var authenticationUri)
            || authenticationUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidDataException("Codex App Server returned an invalid login response.");
        }

        _loginCompletions.TryAdd(
            loginId!,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        return new CodexLoginStart(loginId!, authenticationUri);
    }

    public async Task<bool> WaitForLoginCompletionAsync(string loginId, CancellationToken cancellationToken)
    {
        var completion = _loginCompletions.GetOrAdd(
            loginId,
            static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loginCompletions.TryRemove(loginId, out _);
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        _ = await SendRequestAsync("account/logout", new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    internal static CodexAccountInfo ParseAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account)
            || account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new CodexAccountInfo(false);
        }

        if (account.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex App Server returned an invalid account payload.");
        }

        _ = TryGetString(account, "type", out var accountType);
        _ = TryGetString(account, "email", out var email);
        _ = TryGetString(account, "planType", out var planType);
        return new CodexAccountInfo(true, accountType, email, planType);
    }

    internal static CodexRateLimits ParseRateLimits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimits", out var rateLimits)
            || rateLimits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new CodexRateLimits([]);
        }

        if (rateLimits.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex App Server returned an invalid rate-limit payload.");
        }

        _ = TryGetString(rateLimits, "limitName", out var limitName);
        var windows = new List<TokenUsageWindow>(2);
        AddRateLimitWindow(rateLimits, "primary", limitName ?? "Primary", windows);
        AddRateLimitWindow(rateLimits, "secondary", "Secondary", windows);
        return new CodexRateLimits(windows);
    }

    internal static string FindInstalledCodexExecutable(string localApplicationData)
    {
        var root = Path.Combine(localApplicationData, "OpenAI", "Codex", "bin");
        try
        {
            if (!Directory.Exists(root))
            {
                return "codex.exe";
            }

            return Directory
                .EnumerateDirectories(root)
                .Select(static directory => new
                {
                    Directory = directory,
                    Executable = Path.Combine(directory, "codex.exe"),
                })
                .Where(static candidate => File.Exists(candidate.Executable))
                .OrderByDescending(static candidate => Directory.GetLastWriteTimeUtc(candidate.Directory))
                .Select(static candidate => candidate.Executable)
                .FirstOrDefault() ?? "codex.exe";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return "codex.exe";
        }
    }

    private static string ResolveCodexExecutable()
        => FindInstalledCodexExecutable(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static void AddRateLimitWindow(
        JsonElement rateLimits,
        string propertyName,
        string fallbackLabel,
        ICollection<TokenUsageWindow> windows)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !TryGetDouble(window, "usedPercent", out var usedPercent))
        {
            return;
        }

        int? durationMinutes = null;
        if (TryGetInt64(window, "windowDurationMins", out var duration)
            && duration is > 0 and <= int.MaxValue)
        {
            durationMinutes = (int)duration;
        }

        DateTimeOffset? resetsAt = null;
        if (TryGetInt64(window, "resetsAt", out var resetUnixSeconds))
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 壊れたリセット時刻だけを無視し、使用率自体は表示する。
            }
        }

        var label = durationMinutes is { } minutes
            ? FormatWindowLabel(minutes)
            : fallbackLabel;
        windows.Add(new TokenUsageWindow(
            label,
            Math.Clamp(usedPercent, 0, 100),
            durationMinutes,
            resetsAt));
    }

    private static string FormatWindowLabel(int minutes)
    {
        if (minutes % 10_080 == 0)
        {
            return $"{minutes / 10_080}週間枠";
        }

        if (minutes % 1_440 == 0)
        {
            return $"{minutes / 1_440}日枠";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}時間枠";
        }

        return $"{minutes}分枠";
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await EnsureStartedAsync(timeout.Token).ConfigureAwait(false);
            return await SendRequestCoreAsync(method, parameters, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex App Server did not respond to {method}.");
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized && _process is { HasExited: false })
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _process is { HasExited: false })
            {
                return;
            }

            StopProcess();
            var startInfo = new ProcessStartInfo
            {
                FileName = _executableName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = Utf8WithoutBom,
                StandardOutputEncoding = Utf8WithoutBom,
                StandardErrorEncoding = Utf8WithoutBom,
            };
            startInfo.ArgumentList.Add("app-server");

            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    throw new Win32Exception("Codex App Server could not be started.");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            _process = process;
            _standardInput = process.StandardInput;
            _outputReadTask = ReadOutputAsync(process, _lifetimeCancellation.Token);
            _errorReadTask = DrainErrorsAsync(process, _lifetimeCancellation.Token);

            try
            {
                var version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                _ = await SendRequestCoreAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "taskmetry",
                            ["title"] = "Taskmetry",
                            ["version"] = version,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                await SendNotificationCoreAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            catch
            {
                StopProcess();
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonElement> SendRequestCoreAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate Codex App Server request ID was generated.");
        }

        try
        {
            var envelope = new JsonObject { ["method"] = method, ["id"] = id, ["params"] = parameters };
            await WriteMessageAsync(envelope, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private Task SendNotificationCoreAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
        => WriteMessageAsync(new JsonObject { ["method"] = method, ["params"] = parameters }, cancellationToken);

    private async Task WriteMessageAsync(JsonNode message, CancellationToken cancellationToken)
    {
        var writer = _standardInput ?? throw new IOException("Codex App Server is not running.");
        var json = message.ToJsonString();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleMessage(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            failure = ex;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                if (ReferenceEquals(_process, process))
                {
                    FailPendingRequests(failure ?? new IOException("Codex App Server stopped unexpectedly."));
                    _initialized = false;
                }
            }
        }
    }

    private static async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                // 標準エラーはデッドロック防止のため破棄する。認証情報をログへ残さない。
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常終了。
        }
        catch (IOException)
        {
            // 終了済みプロセスのストリーム切断は呼び出し側で処理する。
        }
    }

    private void HandleMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var idElement)
            && idElement.TryGetInt64(out var id)
            && _pendingRequests.TryGetValue(id, out var completion))
        {
            if (root.TryGetProperty("error", out var error))
            {
                var message = TryGetString(error, "message", out var errorMessage)
                    ? errorMessage!
                    : "Codex App Server request failed.";
                completion.TrySetException(new CodexAppServerException(message));
            }
            else if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetException(new InvalidDataException("Codex App Server returned an invalid response."));
            }

            return;
        }

        if (!TryGetString(root, "method", out var method)
            || method != "account/login/completed"
            || !root.TryGetProperty("params", out var parameters)
            || !TryGetString(parameters, "loginId", out var loginId))
        {
            return;
        }

        var success = parameters.TryGetProperty("success", out var successElement)
            && successElement.ValueKind is JsonValueKind.True;
        if (_loginCompletions.TryGetValue(loginId!, out var loginCompletion))
        {
            loginCompletion.TrySetResult(success);
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var completion in _pendingRequests.Values)
        {
            completion.TrySetException(exception);
        }

        foreach (var completion in _loginCompletions.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private void StopProcess()
    {
        _initialized = false;
        _standardInput = null;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // 既に終了したプロセスは破棄だけ行う。
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        FailPendingRequests(new ObjectDisposedException(nameof(CodexAppServerClient)));
        StopProcess();
        _lifetimeCancellation.Dispose();
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}

internal sealed class CodexAppServerException(string message) : IOException(message);
