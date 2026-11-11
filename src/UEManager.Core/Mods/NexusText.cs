using System.Net;
using System.Text.RegularExpressions;

namespace UEManager.Core.Mods;

public enum NexusBlockKind
{
    Heading,
    Paragraph,
    Item,
}

public sealed record NexusBlock(NexusBlockKind Kind, string Text);

/// <summary>A mod's description as the app shows it: its lines, and its pictures apart.</summary>
public sealed record NexusDescription(IReadOnlyList<NexusBlock> Blocks, IReadOnlyList<string> Images);

/// <summary>
/// Reads a description as Nexus Mods keeps it, BBCode with HTML line breaks, into plain lines: a whole line in bold or
/// in a large size is a heading, [*] starts a list item, and the https pictures of [img] are collected apart. Links
/// keep their text; videos and any other markup are left out.
/// </summary>
public static partial class NexusText
{
    private const char ItemMark = '\u0001';

    public static NexusDescription Parse(string? description)
    {
        var images = new List<string>();
        var text = LineBreak().Replace((description ?? "").Replace("\r", ""), "\n");
        text = Image().Replace(text, match =>
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !images.Contains(url))
                images.Add(url);
            return "\n";
        });
        text = Video().Replace(text, "\n");
        text = LinkWithText().Replace(text, match => match.Groups["text"].Value.Trim().Length > 0 ? match.Groups["text"].Value : match.Groups["href"].Value);
        text = BareLink().Replace(text, match => match.Groups["href"].Value);
        text = ItemStart().Replace(text, "\n" + ItemMark);
        text = ListBound().Replace(text, "\n");

        var blocks = new List<NexusBlock>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Clean(raw);
            if (line.Length == 0 || line == ItemMark.ToString())
                continue;
            if (line[0] == ItemMark)
                blocks.Add(new NexusBlock(NexusBlockKind.Item, line[1..].Trim()));
            else
                blocks.Add(new NexusBlock(IsHeading(raw.Trim(), line) ? NexusBlockKind.Heading : NexusBlockKind.Paragraph, line));
        }
        return new NexusDescription(blocks, images);
    }

    /// <summary>A short line that is bold or large from its start to its end.</summary>
    private static bool IsHeading(string raw, string clean)
    {
        if (clean.Length > 120)
            return false;
        if (WholeLineMarkdownBold().IsMatch(raw))
            return true;
        var match = WholeLineFormatted().Match(raw);
        return match.Success && (match.Groups["open"].Value.Contains("[b]", StringComparison.OrdinalIgnoreCase)
                                 || match.Groups["open"].Value.Contains("[size", StringComparison.OrdinalIgnoreCase));
    }

    private static string Clean(string line)
    {
        var text = Tag().Replace(line, "");
        text = HtmlTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace("**", "");
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"<br\s*/?>|</?p(?:\s[^>]*)?>|</?div(?:\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"\[img[^\]]*\](?<url>.*?)\[/img\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[youtube[^\]]*\].*?\[/youtube\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Video();

    [GeneratedRegex(@"\[url=(?<href>[^\]]*)\](?<text>.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkWithText();

    [GeneratedRegex(@"\[url\](?<href>.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BareLink();

    [GeneratedRegex(@"\[\*\]")]
    private static partial Regex ItemStart();

    [GeneratedRegex(@"\[/\*\]|\[/?list[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ListBound();

    [GeneratedRegex(@"^\*\*[^*]+\*\*$")]
    private static partial Regex WholeLineMarkdownBold();

    [GeneratedRegex(@"^(?<open>(?:\[(?:b|u|i|center|size=[^\]]*|color=[^\]]*|font=[^\]]*)\]\s*)+)[^\[\]]+(?:\s*\[/(?:b|u|i|center|size|color|font)\])+$",
        RegexOptions.IgnoreCase)]
    private static partial Regex WholeLineFormatted();

    [GeneratedRegex(@"\[/?(?:b|i|u|s|size|color|font|center|left|right|quote|spoiler|code|line|hr|heading|indent|sub|sup|ol|ul|li|table|tr|td|th|email|url|img|youtube)(?:=[^\]]*)?\]",
        RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex Spaces();
}
