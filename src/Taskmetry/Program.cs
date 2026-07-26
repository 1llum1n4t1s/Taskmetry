using Avalonia;
using Velopack;

namespace Taskmetry;

internal static class Program
{
    private const string MutexName = @"Local\Taskmetry_SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack の更新適用フックは、Mutex や UI 初期化より先に実行する必要がある。
        VelopackApp.Build().Run();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 終了処理中に所有権が失われていてもプロセス終了は継続する。
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
