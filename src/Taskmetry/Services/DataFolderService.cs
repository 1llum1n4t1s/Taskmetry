using System.Diagnostics;

namespace Taskmetry.Services;

public interface IDataFolderService
{
    void Open();
}

public sealed class DataFolderService : IDataFolderService
{
    private readonly string _directory;

    public DataFolderService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Taskmetry");
    }

    public void Open()
    {
        Directory.CreateDirectory(_directory);
        _ = Process.Start(new ProcessStartInfo("explorer.exe", _directory) { UseShellExecute = true });
    }
}
