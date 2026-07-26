using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void Windows資格情報へ保存して読み戻し後に削除できる()
    {
        var targetName = $"Taskmetry.Tests/{Guid.NewGuid():N}";
        const string secret = "temporary-test-secret";
        var store = new WindowsCredentialStore();

        try
        {
            Assert.False(store.Contains(targetName));

            store.Write(targetName, secret);

            Assert.True(store.Contains(targetName));
            Assert.Equal(secret, store.Read(targetName));
        }
        finally
        {
            store.Delete(targetName);
        }

        Assert.False(store.Contains(targetName));
    }
}
