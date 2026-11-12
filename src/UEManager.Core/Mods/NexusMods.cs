using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEManager.Core.Mods;

/// <summary>A mod on Nexus Mods: the game's short name there, such as "phantombladezero", and the mod's number.</summary>
public sealed record NexusModRef(string Game, int ModId)
{
    public string PageUrl => $"https://www.nexusmods.com/{Game}/mods/{ModId}";

    public string FilePageUrl(int fileId) => $"{PageUrl}?tab=files&file_id={fileId}&nmm=1";

    /// <summary>How a mod installed from Nexus Mods is marked in mods.json (<see cref="ModRecord.CatalogId"/>), so an update replaces it.</summary>
    public string CatalogId => $"{NexusMods.CatalogPrefix}{Game}/{ModId}";
}

/// <summary>One file of a mod on Nexus Mods.</summary>
/// <param name="Category">MAIN, UPDATE, OPTIONAL, MISCELLANEOUS, OLD_VERSION or ARCHIVED.</param>
/// <param name="Date">When it was uploaded, in seconds since 1970.</param>
public sealed record NexusFile(int FileId, string Name, string Version, string Category, long Date, long Size, bool Primary)
{
    /// <summary>Still offered on the mod's page: not an old version and not archived.</summary>
    public bool IsCurrent => Category is not ("OLD_VERSION" or "ARCHIVED" or "DELETED");
}

/// <param name="Card">What the mod's card on the Mods page shows.</param>
public sealed record NexusModInfo(NexusModRef Ref, string Name, IReadOnlyList<NexusFile> Files, NexusListedMod? Card = null);

/// <summary>A "Mod Manager Download" link from the site: nxm://game/mods/1/files/2?key=…&amp;expires=….</summary>
public sealed record NxmLink(NexusModRef Mod, int FileId, string Key, string Expires);

/// <summary>Nexus Mods gives a direct download only to Premium members; others start it on the mod's page.</summary>
public sealed class NexusPremiumRequiredException() : ModException($"Nexus Mods gives direct downloads only to Premium members.");

/// <summary>
/// Nexus Mods: mod links, the files of a mod and their downloads. Reading a mod's files needs no account (the public
/// GraphQL API); a download needs the player's own API key, and without Premium the key from a Mod Manager Download link.
/// </summary>
public static partial class NexusMods
{
    public const string CatalogPrefix = "nexusmods:";

    public const string ApiKeyPage = "https://www.nexusmods.com/settings/api-keys";

    private const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";

    private const string RestUrl = "https://api.nexusmods.com/v1";

    private static readonly ConcurrentDictionary<string, int> GameIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod in a link such as "https://www.nexusmods.com/phantombladezero/mods/260?tab=files"; null for other text.</summary>
    public static NexusModRef? ParseUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ModUrl().Match(text.Trim()) is not { Success: true } match)
            return null;
        return int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? new NexusModRef(match.Groups["game"].Value.ToLowerInvariant(), id)
            : null;
    }

    /// <summary>The game's short name in a mod page address such as a game profile's modPageUrl; null without one.</summary>
    public static string? GameOf(string? modPageUrl) =>
        modPageUrl is null || GameUrl().Match(modPageUrl) is not { Success: true } match ? null : match.Groups["game"].Value.ToLowerInvariant();

    public static bool IsNxm(string? text) => text?.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) == true;

    public static NxmLink? ParseNxm(string? text)
    {
        if (!IsNxm(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts is not ["mods", var mod, "files", var file]
            || !int.TryParse(mod, NumberStyles.None, CultureInfo.InvariantCulture, out var modId)
            || !int.TryParse(file, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId))
            return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "", StringComparer.OrdinalIgnoreCase);
        return query.TryGetValue("key", out var key) && query.TryGetValue("expires", out var expires) && key.Length > 0
            ? new NxmLink(new NexusModRef(uri.Host.ToLowerInvariant(), modId), fileId, key, expires)
            : null;
    }

    /// <summary>
    /// The Nexus Mods mod <paramref name="mod"/> came from: the one it was installed from by link, or else the number in
    /// its download's file name; null for any other mod.
    /// </summary>
    public static NexusModRef? RefOf(ModRecord mod, string? game)
    {
        if (mod.CatalogId?.StartsWith(CatalogPrefix, StringComparison.OrdinalIgnoreCase) == true
            && mod.CatalogId[CatalogPrefix.Length..].Split('/') is [var g, var id]
            && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return new NexusModRef(g, number);
        if (mod.CatalogId is null && !mod.Recommended && game is not null
            && int.TryParse(ModNaming.SiteModId(mod.SourceFileName), NumberStyles.None, CultureInfo.InvariantCulture, out var siteId))
            return new NexusModRef(game, siteId);
        return null;
    }

    /// <summary>
    /// The file to install for the mod: the newest current file with the same name as the installed one, as authors keep
    /// a file's name across its versions; else the file the author marks as the main one, else the newest main file.
    /// </summary>
    /// <param name="installedFileName">The installed file's name on the site; null for a new install.</param>
    public static NexusFile? PickFile(IReadOnlyList<NexusFile> files, string? installedFileName)
    {
        var current = files.Where(f => f.IsCurrent).OrderByDescending(f => f.Date).ToList();
        if (installedFileName is not null && current.FirstOrDefault(f => SameName(f.Name, installedFileName)) is { } same)
            return same;
        return current.FirstOrDefault(f => f.Primary)
            ?? current.FirstOrDefault(f => f.Category == "MAIN")
            ?? current.FirstOrDefault();
    }

    /// <summary>
    /// The update for a mod installed from <paramref name="sourceFileName"/>, or null when it has the newest file. The
    /// installed file is found by its name and version on the site; it is out of date when the file to install is newer.
    /// </summary>
    public static NexusFile? FindUpdate(IReadOnlyList<NexusFile> files, string? sourceFileName, string? recordVersion)
    {
        var site = ModNaming.SiteFile(sourceFileName);
        if (PickFile(files, site?.Name) is not { } latest)
            return null;
        var installed = site is { } s
            ? files.Where(f => SameName(f.Name, s.Name) && string.Equals(f.Version, s.Version, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Date).FirstOrDefault()
            : null;
        if (installed is not null)
            return latest.FileId != installed.FileId && latest.Date > installed.Date ? latest : null;
        return ModVersion.IsUpdate(site?.Version ?? recordVersion, latest.Version) ? latest : null;
    }

    /// <summary>The same file across versions: names compared without case, and with "_" as a space, as file names have them.</summary>
    public static bool SameName(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => Whitespace().Replace(name.Replace('_', ' '), " ").Trim();

    /// <summary>
    /// A file name for the download that keeps the site's pattern (see <see cref="ModNaming.SiteFile"/>), so the mod's
    /// page and its updates stay known from it.
    /// </summary>
    public static string DownloadFileName(NexusModRef mod, NexusFile file, string extension)
    {
        var name = Whitespace().Replace(FileSystem.SafeName(file.Name, "mod").Replace('_', ' '), " ").Trim();
        var version = Whitespace().Replace(FileSystem.SafeName(file.Version, "0"), "-").Replace('_', '-');
        var time = DateTimeOffset.FromUnixTimeSeconds(file.Date).UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm'Z'", CultureInfo.InvariantCulture);
        return $"{name} {mod.ModId} {version} {time} f{file.FileId}{extension}";
    }

    /// <summary>The mod and file of a download named by <see cref="DownloadFileName"/>; null for any other name.</summary>
    public static (int ModId, int FileId)? DownloadIds(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var last = stem[(stem.LastIndexOf(' ') + 1)..];
        return last.Length > 1 && last[0] == 'f'
            && int.TryParse(last[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var fileId)
            && int.TryParse(ModNaming.SiteModId(fileName), NumberStyles.None, CultureInfo.InvariantCulture, out var modId)
                ? (modId, fileId)
                : null;
    }

    /// <summary>The mod's name, files and card, from the public API; no account needed.</summary>
    public static async Task<NexusModInfo> GetModAsync(HttpClient http, NexusModRef mod, CancellationToken cancellation = default)
    {
        var gameId = await GameIdAsync(http, mod.Game, cancellation);
        var data = await QueryAsync(http,
            $$"""
            query { legacyMods(ids:[{gameId:{{gameId}}, modId:{{mod.ModId}}}]) { nodes { {{ListedFields}} } }
              modFiles(modId:{{mod.ModId}}, gameId:"{{gameId}}") { fileId name version category date sizeInBytes primary } }
            """, cancellation);
        var nodes = data.GetProperty("legacyMods").GetProperty("nodes");
        if (nodes.GetArrayLength() == 0)
            throw new ModException($"Nexus Mods has no mod {mod.ModId} for this game. Check the link.");
        var files = data.GetProperty("modFiles").EnumerateArray().Select(f => new NexusFile(
            f.GetProperty("fileId").GetInt32(),
            f.GetProperty("name").GetString() ?? "",
            f.GetProperty("version").GetString() ?? "",
            f.GetProperty("category").GetString() ?? "",
            f.GetProperty("date").GetInt64(),
            long.TryParse(f.GetProperty("sizeInBytes").ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : 0,
            f.GetProperty("primary").ValueKind == JsonValueKind.Number ? f.GetProperty("primary").GetInt32() != 0 : f.GetProperty("primary").GetBoolean()))
            .ToList();
        return new NexusModInfo(mod, nodes[0].GetProperty("name").GetString() ?? $"Mod {mod.ModId}", files, Listed(nodes[0]));
    }

    /// <summary>
    /// Where to download a file. Premium members get the address with their API key alone; others need the key and
    /// expiry of a Mod Manager Download link (<paramref name="nxm"/>), else <see cref="NexusPremiumRequiredException"/>.
    /// </summary>
    public static async Task<string> DownloadUrlAsync(HttpClient http, string apiKey, NexusModRef mod, int fileId, NxmLink? nxm,
        CancellationToken cancellation = default)
    {
        var url = $"{RestUrl}/games/{mod.Game}/mods/{mod.ModId}/files/{fileId}/download_link.json";
        if (nxm is not null)
            url += $"?key={Uri.EscapeDataString(nxm.Key)}&expires={Uri.EscapeDataString(nxm.Expires)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("apikey", apiKey.Trim());
        using var response = await SendAsync(http, request, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ModException($"Nexus Mods did not accept the API key. Check it in Settings.");
        if (response.StatusCode == HttpStatusCode.Forbidden && nxm is null)
            throw new NexusPremiumRequiredException();
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.NotFound)
            throw new ModException($"Nexus Mods refused the download link. It may have expired: click Mod Manager Download again.");
        if (!response.IsSuccessStatusCode)
            throw new ModException($"Nexus Mods answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        var links = await ReadJsonAsync(response, cancellation);
        foreach (var link in links.EnumerateArray())
        {
            if (link.TryGetProperty("URI", out var uri) && uri.GetString() is { Length: > 0 } address)
                return address;
        }
        throw new ModException($"Nexus Mods gave no download address for this file.");
    }

    /// <summary>The account an API key belongs to, and whether it is Premium.</summary>
    public static async Task<(string Name, bool Premium)> ValidateAsync(HttpClient http, string apiKey, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RestUrl}/users/validate.json");
        request.Headers.Add("apikey", apiKey.Trim());
        using var response = await SendAsync(http, request, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ModException($"Nexus Mods did not accept the API key. Check it in Settings.");
        if (!response.IsSuccessStatusCode)
            throw new ModException($"Nexus Mods answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        var user = await ReadJsonAsync(response, cancellation);
        return (user.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            user.TryGetProperty("is_premium", out var premium) && premium.ValueKind == JsonValueKind.True);
    }

    private static async Task<int> GameIdAsync(HttpClient http, string game, CancellationToken cancellation)
    {
        if (GameIds.TryGetValue(game, out var known))
            return known;
        var data = await QueryAsync(http, $$"""query { game(domainName:"{{game}}") { id } }""", cancellation);
        if (data.GetProperty("game").ValueKind != JsonValueKind.Object)
            throw new ModException($"Nexus Mods has no game \"{game}\". Check the link.");
        var id = data.GetProperty("game").GetProperty("id").GetInt32();
        GameIds[game] = id;
        return id;
    }

    private static async Task<JsonElement> QueryAsync(HttpClient http, string query, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl) { Content = JsonContent.Create(new { query }) };
        using var response = await SendAsync(http, request, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new ModException($"Nexus Mods answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        var root = await ReadJsonAsync(response, cancellation);
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new ModException($"Nexus Mods could not answer: {message}");
        }
        return root.GetProperty("data");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellation)
    {
        try
        {
            return await http.SendAsync(request, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new ModException($"Nexus Mods could not be reached: {ex.Message}");
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ModException($"Nexus Mods could not answer: {ex.Message}");
        }
    }

    [GeneratedRegex(@"nexusmods\.com/(?<game>[A-Za-z0-9_-]+)/mods/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModUrl();

    [GeneratedRegex(@"nexusmods\.com/(?<game>[A-Za-z0-9_-]+)/mods/", RegexOptions.IgnoreCase)]
    private static partial Regex GameUrl();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
