using CommunityToolkit.Mvvm.Input;
using UEManager.Core.Mods;

namespace UEManager.UI.ViewModels;

/// <summary>The Browse page's part of the main view model, and the line on the Mods page that points to the Loaders page.</summary>
public sealed partial class MainViewModel
{
    private NexusBrowseViewModel? browse;

    /// <summary>The Browse page; null for a game with no Nexus Mods page.</summary>
    public NexusBrowseViewModel? Browse => browse ??= NexusGame is { } game
        ? new NexusBrowseViewModel(this, Http, new FreeTranslator(Http, paths.TranslationsFile), game)
        : null;

    public bool HasBrowse => NexusGame is not null;

    /// <summary>A loader on the Loaders page has an update, or two of them clash: the Mods page says so in one line.</summary>
    public bool LoadersNeedAttention => IsRecommendedUpdateAvailable || HasOldScriptLoader;

    [RelayCommand]
    private void ShowLoaders() => SelectedPage = "Ue4ss";

    /// <summary>The installed mod that came from this Nexus Mods mod; null when none did.</summary>
    internal ModRecord? InstalledFromNexus(int modId) =>
        manager is null || NexusGame is not { } game ? null : OwnMods.FirstOrDefault(m => NexusMods.RefOf(m, game) == new NexusModRef(game, modId));

    internal bool HasNexusUpdate(ModRecord mod) => nexusUpdates.ContainsKey(mod.Id);

    /// <summary>Install from the Browse page, as a pasted link does: an installed mod gets its update instead.</summary>
    internal async Task InstallFromBrowseAsync(int modId)
    {
        if (EnsureGame() && NexusGame is { } game)
            await ImportNexusModAsync(new NexusModRef(game, modId));
    }
}
