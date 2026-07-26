using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Velopack;
using Velopack.Sources;
using VelopackUpdateDialog;

namespace Taskmetry.Services;

/// <summary>署名済み Velopack パッケージを Cloudflare R2 から確認・適用する。</summary>
internal sealed class UpdateService
{
    internal const string UpdateBaseUrl = "https://taskmetry.kagayoi.com";
    private static readonly TimeSpan AutomaticCheckTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public async Task CheckAsync(Window owner, bool manualCheck, CancellationToken cancellationToken)
    {
        var timeout = manualCheck ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        if (!await _operationLock.WaitAsync(timeout, cancellationToken))
        {
            return;
        }

        try
        {
            var source = new SimpleWebSource(UpdateBaseUrl);
            var manager = new UpdateManager(source);

            // dotnet run など Velopack 管理外の開発実行では、起動時通信を行わない。
            if (!manager.IsInstalled && !manualCheck)
            {
                return;
            }

            var options = new UpdateDialogOptions
            {
                Strings = JapaneseUpdateDialogStrings.Instance,
                AccentBrush = Brushes.Cyan,
                AllowIgnoreVersion = false,
                AllowCloseDuringDownload = true,
                SuppressUpToDateOnAutoCheck = true,
            };
            options.ErrorOccurred += ex =>
                Debug.WriteLine($"Taskmetry update error: {ex.GetType().Name}: {ex.Message}");

            using var automaticTimeout = manualCheck
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            automaticTimeout?.CancelAfter(AutomaticCheckTimeout);
            var effectiveToken = automaticTimeout?.Token ?? cancellationToken;

            await UpdateDialogWindow.ShowAsync(
                owner,
                manager,
                options,
                manualCheck,
                effectiveToken);
        }
        catch (OperationCanceledException) when (!manualCheck && !cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine("Taskmetry の自動更新確認は 30 秒でタイムアウトしました。");
        }
        catch (Exception ex)
        {
            // 手動確認の通信・適用エラーはダイアログ側にも表示される。アプリ本体は継続する。
            Debug.WriteLine($"Taskmetry update check failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }
}
