using System.ComponentModel;
using System.Diagnostics;

namespace Taskmetry.Services;

public interface IExternalBrowserService
{
    void Open(Uri uri);
}

public sealed class ExternalBrowserService : IExternalBrowserService
{
    public void Open(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Web URL must use HTTP or HTTPS.", nameof(uri));
        }

        var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        if (process is null)
        {
            throw new Win32Exception("既定のWebブラウザーを起動できませんでした。");
        }
    }
}
