using UEManager.Core.Mods;

namespace UEManager.Core.Tests;

/// <summary>Nexus Mods links, and which file of a mod is its update, with file lists as the site gives them.</summary>
public class NexusModsTests
{
    [Theory]
    [InlineData("https://www.nexusmods.com/phantombladezero/mods/260", "phantombladezero", 260)]
    [InlineData("  https://www.nexusmods.com/phantombladezero/mods/260?tab=files&file_id=2285 ", "phantombladezero", 260)]
    [InlineData("nexusmods.com/PhantomBladeZero/mods/7/", "phantombladezero", 7)]
    public void A_mod_link_names_the_game_and_the_mod(string link, string game, int modId)
    {
        Assert.Equal(new NexusModRef(game, modId), NexusMods.ParseUrl(link));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.nexusmods.com/phantombladezero")]
    [InlineData("https://example.com/phantombladezero/mods/260")]
    public void Other_text_is_no_mod_link(string text) => Assert.Null(NexusMods.ParseUrl(text));

    [Fact]
    public void A_mod_manager_download_link_has_the_file_and_its_key()
    {
        var link = NexusMods.ParseNxm("nxm://phantombladezero/mods/260/files/2285?key=a%2Bb&expires=1794200000&user_id=1");
        Assert.Equal(new NxmLink(new NexusModRef("phantombladezero", 260), 2285, "a+b", "1794200000"), link);
        Assert.Null(NexusMods.ParseNxm("nxm://phantombladezero/mods/260/files/2285"));
    }

    [Fact]
    public void The_game_comes_from_the_profiles_mod_page() =>
        Assert.Equal("phantombladezero", NexusMods.GameOf("https://www.nexusmods.com/phantombladezero/mods/{id}"));

    [Fact]
    public void A_mod_is_known_by_its_link_or_by_its_downloads_name()
    {
        Assert.Equal(new NexusModRef("phantombladezero", 260),
            NexusMods.RefOf(new ModRecord { CatalogId = "nexusmods:phantombladezero/260" }, "phantombladezero"));
        Assert.Equal(new NexusModRef("phantombladezero", 260),
            NexusMods.RefOf(new ModRecord { SourceFileName = "Wuchang Volice 2 260 2 2026-11-01T16-20Z azp3elF3r.zip" }, "phantombladezero"));
        Assert.Null(NexusMods.RefOf(new ModRecord { SourceFileName = "超广角.zip" }, "phantombladezero"));
        Assert.Null(NexusMods.RefOf(new ModRecord { CatalogId = "owner/repo", SourceFileName = "Mods Menu 149 0.7.2 2026-11-05T10-00Z AbC123xyz.7z" }, "phantombladezero"));
    }

    private static readonly NexusFile[] Wuchang =
    [
        new(621, "Wuchang Volice", "1", "OLD_VERSION", 1793473211, 1, false),
        new(855, "Wuchang Volice 2", "2", "OLD_VERSION", 1793550008, 1, false),
        new(2285, "Wuchang Volice 2", "3", "MAIN", 1793982753, 1, true),
    ];

    [Fact]
    public void A_newer_file_of_the_same_name_is_the_update()
    {
        Assert.Equal(2285, NexusMods.FindUpdate(Wuchang, "Wuchang Volice 2 260 2 2026-11-01T16-20Z azp3elF3r.zip", "2")?.FileId);
        Assert.Null(NexusMods.FindUpdate(Wuchang, "Wuchang Volice 2 260 3 2026-11-07T09-12Z abc.zip", "3"));
    }

    [Fact]
    public void A_file_the_author_renamed_is_followed_by_the_main_file()
    {
        NexusFile[] betterImpact =
        [
            new(647, "BetterImpact V2.0", "5", "OLD_VERSION", 1793477508, 1, false),
            new(2604, "BetterImpact V3.0", "6", "MAIN", 1794101640, 1, true),
        ];
        Assert.Equal(2604, NexusMods.FindUpdate(betterImpact, "BetterImpact V2.0 181 5 2026-10-31T20-11Z 25qxOUbSd.zip", "5")?.FileId);
    }

    [Fact]
    public void An_optional_file_that_is_still_offered_updates_by_its_own_name()
    {
        NexusFile[] markers =
        [
            new(1894, "PBZMapMarkers 1.8.2 Fog", "1.8.2", "OPTIONAL", 1793863693, 1, false),
            new(1802, "PBZMapMarkers 1.8.1 Fog", "1.8.1", "ARCHIVED", 1793835229, 1, false),
            new(2675, "Map Collectible Markers UE4SS", "2.2.1", "MAIN", 1794128373, 1, true),
        ];
        Assert.Equal(2675, NexusMods.PickFile(markers, null)?.FileId);
        Assert.Equal(2675, NexusMods.PickFile(markers, "PBZMapMarkers 1.8.1 Fog")?.FileId);
        Assert.Equal(1894, NexusMods.PickFile(markers, "PBZMapMarkers_1.8.2_Fog")?.FileId);
    }

    [Fact]
    public void The_download_keeps_a_name_the_mod_page_and_updates_are_known_by()
    {
        var mod = new NexusModRef("phantombladezero", 260);
        var name = NexusMods.DownloadFileName(mod, Wuchang[2], ".zip");
        Assert.Equal("260", ModNaming.SiteModId(name));
        Assert.Equal(("Wuchang Volice 2", "3"), ModNaming.SiteFile(name));
        Assert.Null(NexusMods.FindUpdate(Wuchang, name, null));
    }
}
