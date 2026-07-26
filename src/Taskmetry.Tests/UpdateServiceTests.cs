using Taskmetry.Services;

namespace Taskmetry.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void UpdateBaseUrl_UsesExpectedSecureDistributionHost()
    {
        var uri = new Uri(UpdateService.UpdateBaseUrl);

        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal("taskmetry.kagayoi.com", uri.Host);
        Assert.Empty(uri.UserInfo);
        Assert.Equal("/", uri.AbsolutePath);
    }
}
