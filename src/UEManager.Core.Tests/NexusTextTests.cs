using System.Text.Json;
using UEManager.Core.Mods;

namespace UEManager.Core.Tests;

/// <summary>Mod descriptions from Nexus Mods, BBCode as the site keeps it, read into the lines the preview shows.</summary>
public class NexusTextTests
{
    [Fact]
    public void Changes_count_each_version_and_line_once_newest_first()
    {
        using var files = JsonDocument.Parse("""
            [
              { "version": "1.8.2", "date": 100, "changelogText": ["Fixed the [b]fog[/b] markers"] },
              { "version": "1.8.3", "date": 300, "changelogText": ["Starts only once", "Starts only once"] },
              { "version": "1.8.3", "date": 200, "changelogText": ["Starts only once"] },
              { "version": "1.0", "date": 50, "changelogText": [] },
              { "version": "0.9", "date": 10, "changelogText": ["First release"] }
            ]
            """);

        var changes = NexusMods.Changes(files.RootElement, 2);

        Assert.Equal(["1.8.3", "1.8.2"], changes.Select(c => c.Version));
        Assert.Equal(["Starts only once"], changes[0].Lines);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(300), changes[0].Date);
        Assert.Equal(["Fixed the fog markers"], changes[1].Lines);
    }

    [Fact]
    public void Headings_items_and_paragraphs_come_from_the_markup()
    {
        var description = NexusText.Parse("""
            [size=5][b]Map Collectible Markers[/b][/size]
            <br />
            <br />[color=#ff9900][b]Game update:[/b][/color] the game no longer loads &quot;pak&quot; mods.
            <br />[list]
            <br />[*][b]2.2.6[/b]: needs only [url=https://www.nexusmods.com/phantombladezero/mods/13]UE4SS[/url].[/*]
            <br />[/list]**Install one of the two**
            """);

        Assert.Equal(
        [
            new NexusBlock(NexusBlockKind.Heading, "Map Collectible Markers"),
            new NexusBlock(NexusBlockKind.Paragraph, "Game update: the game no longer loads \"pak\" mods."),
            new NexusBlock(NexusBlockKind.Item, "2.2.6: needs only UE4SS."),
            new NexusBlock(NexusBlockKind.Heading, "Install one of the two"),
        ], description.Blocks);
    }

    [Fact]
    public void Pictures_are_collected_apart_and_videos_left_out()
    {
        var description = NexusText.Parse(
            "Before[img]https://i.imgur.com/a.png[/img]<br />[img width=300]http://old.example/b.png[/img][youtube]abc[/youtube]After[img]https://i.imgur.com/a.png[/img]");

        Assert.Equal(["https://i.imgur.com/a.png"], description.Images);
        Assert.Equal(["Before", "After"], description.Blocks.Select(b => b.Text));
    }

    [Fact]
    public void Brackets_that_are_no_markup_stay()
    {
        var description = NexusText.Parse("[Optional] file: [b]Teleport[/b]");

        Assert.Equal([new NexusBlock(NexusBlockKind.Paragraph, "[Optional] file: Teleport")], description.Blocks);
    }
}
