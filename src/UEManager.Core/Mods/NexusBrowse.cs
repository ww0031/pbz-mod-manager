using System.Globalization;
using System.Net;
using System.Text.Json;

namespace UEManager.Core.Mods;

/// <summary>How the game's mod list on Nexus Mods is ordered, the most first.</summary>
public enum NexusSort
{
    Endorsements,
    Downloads,
    Updated,
    Created,
}

/// <summary>A mod in the game's list on Nexus Mods, as its card shows it.</summary>
public sealed record NexusListedMod(int ModId, string Name, string Summary, string? Picture, string? Thumbnail, string? Author,
    string Version, int Endorsements, int Downloads, DateTimeOffset? Updated, string? Category);

/// <summary>One page of the list, and how many mods the whole list has.</summary>
public sealed record NexusModList(int Total, IReadOnlyList<NexusListedMod> Mods);

/// <summary>What changed in one version of a mod, as its author wrote it.</summary>
public sealed record NexusChange(string Version, DateTimeOffset Date, IReadOnlyList<string> Lines);

public static partial class NexusMods
{
    /// <summary>
    /// One page of the game's mods, from the public API; no account needed. Adult content is never listed: the site
    /// leaves it out, and a mod not marked as safe that comes through anyway is dropped here.
    /// </summary>
    public static async Task<NexusModList> ListModsAsync(HttpClient http, string game, NexusSort sort, string? search, int offset, int count,
        CancellationToken cancellation = default)
    {
        var name = string.IsNullOrWhiteSpace(search) ? "" : $", name: [{{ value: {JsonSerializer.Serialize(search.Trim())}, op: WILDCARD }}]";
        var data = await QueryAsync(http,
            $$"""
            query { mods(filter: { gameDomainName: [{ value: {{JsonSerializer.Serialize(game)}} }], adultContent: [{ value: false }]{{name}} },
              sort: [{ {{SortField(sort)}}: { direction: DESC } }], offset: {{offset}}, count: {{count}}) {
              totalCount nodes { modId name summary pictureUrl thumbnailLargeUrl thumbnailUrl adultContent author version
                endorsements downloads updatedAt modCategory { name } } } }
            """, cancellation);
        var mods = data.GetProperty("mods");
        var list = mods.GetProperty("nodes").EnumerateArray()
            .Where(IsSafe)
            .Select(m => new NexusListedMod(
                m.GetProperty("modId").GetInt32(),
                Text(m, "name") ?? "",
                Text(m, "summary") ?? "",
                Text(m, "pictureUrl"),
                Text(m, "thumbnailLargeUrl") ?? Text(m, "thumbnailUrl"),
                Text(m, "author"),
                Text(m, "version") ?? "",
                Number(m, "endorsements"),
                Number(m, "downloads"),
                DateTimeOffset.TryParse(Text(m, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated) ? updated : null,
                m.TryGetProperty("modCategory", out var category) && category.ValueKind == JsonValueKind.Object ? Text(category, "name") : null))
            .ToList();
        return new NexusModList(mods.GetProperty("totalCount").GetInt32(), list);
    }

    /// <summary>The mod's description as the site keeps it (BBCode); null for a mod not marked as safe, which is never shown.</summary>
    public static async Task<string?> DescriptionAsync(HttpClient http, NexusModRef mod, CancellationToken cancellation = default)
    {
        var gameId = await GameIdAsync(http, mod.Game, cancellation);
        var data = await QueryAsync(http,
            $$"""query { legacyMods(ids:[{gameId:{{gameId}}, modId:{{mod.ModId}}}]) { nodes { description adultContent } } }""", cancellation);
        var nodes = data.GetProperty("legacyMods").GetProperty("nodes");
        if (nodes.GetArrayLength() == 0)
            throw new ModException($"Nexus Mods has no mod {mod.ModId} for this game. Check the link.");
        return IsSafe(nodes[0]) ? nodes[0].GetProperty("description").GetString() ?? "" : null;
    }

    /// <summary>What changed in the mod's newest versions, newest first, from the public API; no account needed.</summary>
    public static async Task<IReadOnlyList<NexusChange>> ChangesAsync(HttpClient http, NexusModRef mod, int versions,
        CancellationToken cancellation = default)
    {
        var gameId = await GameIdAsync(http, mod.Game, cancellation);
        var data = await QueryAsync(http,
            $$"""query { modFiles(modId:{{mod.ModId}}, gameId:"{{gameId}}") { version date changelogText } }""", cancellation);
        return Changes(data.GetProperty("modFiles"), versions);
    }

    /// <summary>
    /// The site gives a version's lines with each of its files, in BBCode, so a version and a line count once here.
    /// A version without lines is left out.
    /// </summary>
    internal static IReadOnlyList<NexusChange> Changes(JsonElement files, int versions) =>
        files.EnumerateArray()
            .Select(f => (Version: Text(f, "version") ?? "", Date: f.GetProperty("date").GetInt64(),
                Lines: f.TryGetProperty("changelogText", out var lines) && lines.ValueKind == JsonValueKind.Array
                    ? lines.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String)
                        .SelectMany(l => NexusText.Parse(l.GetString()).Blocks.Select(b => b.Text))
                    : Enumerable.Empty<string>()))
            .GroupBy(f => f.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NexusChange(g.Key, DateTimeOffset.FromUnixTimeSeconds(g.Max(f => f.Date)), g.SelectMany(f => f.Lines).Distinct().ToList()))
            .Where(c => c.Lines.Count > 0)
            .OrderByDescending(c => c.Date)
            .Take(versions)
            .ToList();

    /// <summary>Only a mod the site marks as not adult content counts as safe; an unknown mark does not.</summary>
    internal static bool IsSafe(JsonElement mod) =>
        mod.TryGetProperty("adultContent", out var adult) && adult.ValueKind == JsonValueKind.False;

    private static string SortField(NexusSort sort) => sort switch
    {
        NexusSort.Downloads => "downloads",
        NexusSort.Updated => "updatedAt",
        NexusSort.Created => "createdAt",
        _ => "endorsements",
    };

    /// <summary>A text of the site, which may carry HTML entities such as &amp;#039;.</summary>
    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? WebUtility.HtmlDecode(text).Trim()
            : null;

    private static int Number(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
