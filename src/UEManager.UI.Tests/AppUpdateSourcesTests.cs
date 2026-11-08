using UEManager.UI.Services;
using Xunit;

namespace UEManager.UI.Tests;

public class AppUpdateSourcesTests
{
    [Fact]
    public void The_update_check_reads_the_releases_of_this_modified_version()
    {
        Assert.Equal([AppInfo.LatestReleaseUrl], AppInfo.LatestReleaseUrls);
        Assert.StartsWith(AppInfo.ForkUrl.Replace("https://github.com/", "https://api.github.com/repos/"), AppInfo.LatestReleaseUrl);
    }
}
