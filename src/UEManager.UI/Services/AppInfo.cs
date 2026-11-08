using System.Reflection;

namespace UEManager.UI.Services;

internal static class AppInfo
{
    public const string Name = "影之刃模组管理器";

    /// <summary>How the release zips start, as publish.ps1 names them.</summary>
    public const string ReleaseFilePrefix = "uemm";

    /// <summary>Where the original lives; works based on it keep linking here (NOTICE.md, term 1).</summary>
    public const string ProjectUrl = "https://github.com/KintaroModding/uemodmanager";

    /// <summary>Where this modified version lives; its releases are the app updates.</summary>
    public const string ForkUrl = "https://github.com/ww0031/pbz-mod-manager";

    /// <summary>GitHub's answer for the newest release of this modified version.</summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/ww0031/pbz-mod-manager/releases/latest";

    /// <summary>The newest release, read at every start for the update check.</summary>
    public static readonly IReadOnlyList<string> LatestReleaseUrls = [LatestReleaseUrl];

    public const string ReleasesPageUrl = ForkUrl + "/releases/latest";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>A pre-release build, as set by PreRelease in Directory.Build.props.</summary>
    public static bool IsPreRelease { get; } = IsSwitchedOn("PreRelease");

    /// <summary>The openmods page is shown, as set by Catalog in Directory.Build.props. Its code is always built in.</summary>
    public static bool IsCatalogOn { get; } = IsSwitchedOn("Catalog");

    private static bool IsSwitchedOn(string name) =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute => attribute.Key == name && string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase));
}
