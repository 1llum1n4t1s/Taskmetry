using Taskmetry.Models;
using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void 破損設定を退避してから既定値で回復する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{");
            var service = new SettingsService(path);

            var result = service.Load();

            Assert.Equal(SettingsLoadStatus.Corrupt, result.Status);
            Assert.True(result.RecoveryCopyCreated);
            Assert.True(result.CanSave);
            Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*"));

            service.Save(new AppSettings { ShowCpu = false, FirstRun = false });
            Assert.False(service.Current.ShowCpu);
            Assert.False(service.Current.FirstRun);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 一時的な読込失敗後は既存設定保護のため保存を停止する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"firstRun\":false,\"showCpu\":false}");
            var service = new SettingsService(path);

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = service.Load();

                Assert.Equal(SettingsLoadStatus.IoError, result.Status);
                Assert.False(result.CanSave);
                Assert.Throws<IOException>(() => service.Save(new AppSettings()));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 保存では一意な一時ファイルを使い残骸を残さない()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var service = new SettingsService(path);
            _ = service.Load();

            service.Save(new AppSettings { PreferredWidthPixels = 900, FirstRun = false });

            Assert.Equal(900, service.Current.PreferredWidthPixels);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Taskmetry.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
