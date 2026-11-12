using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using UEManager.Core;
using UEManager.Core.Mods;
using UEManager.Core.Storage;
using UEManager.UI.Catalog;

namespace UEManager.UI.ViewModels;

/// <summary>
/// The Browse page's part of the main view model, the Nexus Mods cards of the installed mods on the Mods page, and the
/// line on the Mods page that points to the Loaders page.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How long an install waits for its name in the app's language before it takes the site's name.</summary>
    private static readonly TimeSpan NameTranslationWait = TimeSpan.FromSeconds(10);

    private NexusBrowseViewModel? browse;
    private FreeTranslator? translator;
    private Dictionary<string, NexusListedMod>? nexusCards;

    /// <summary>The cards' names and summaries in the app's language, by the language and the text as the site has it.</summary>
    private readonly Dictionary<string, string> cardTexts = new(StringComparer.Ordinal);

    private FreeTranslator Translator => translator ??= new FreeTranslator(Http, paths.TranslationsFile);

    /// <summary>The Browse page; null for a game with no Nexus Mods page.</summary>
    public NexusBrowseViewModel? Browse => browse ??= NexusGame is { } game ? new NexusBrowseViewModel(this, Http, Translator, game) : null;

    public bool HasBrowse => NexusGame is not null;

    /// <summary>The Mods page shows a card per mod, with its picture and summary from Nexus Mods; else the compact table.</summary>
    public bool ModsAsCards => !settings.ModsCompact;

    public bool ModsAsTable => settings.ModsCompact;

    /// <summary>The two buttons that switch the Mods page between its cards and its table.</summary>
    public IReadOnlyList<TagFilterItem> ModsViewModes =>
    [
        new(L.T("Preview"), ModsAsCards, new RelayCommand(() => SetModsCompact(false))),
        new(L.T("Compact"), ModsAsTable, new RelayCommand(() => SetModsCompact(true))),
    ];

    /// <summary>The cards' pictures, shared with the Browse page; null for a game with no Nexus Mods page.</summary>
    internal RemoteImages? Covers => Browse?.Images;

    /// <summary>A loader on the Loaders page has an update, or two of them clash: the Mods page says so in one line.</summary>
    public bool LoadersNeedAttention => IsRecommendedUpdateAvailable || HasOldScriptLoader;

    /// <summary>
    /// The Nexus Mods cards of the installed mods, by the mod's mark (<see cref="NexusModRef.CatalogId"/>). Kept on disk,
    /// so the Mods page shows them before the site answers; the update check renews them.
    /// </summary>
    private Dictionary<string, NexusListedMod> NexusCards => nexusCards ??= LoadNexusCards();

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

    /// <summary>The installed mod's card; null for a mod not known on Nexus Mods, or not read there yet.</summary>
    internal NexusListedMod? NexusCard(ModRecord mod) =>
        !mod.Recommended && NexusMods.RefOf(mod, NexusGame) is { } found && NexusCards.TryGetValue(found.CatalogId, out var card) ? card : null;

    /// <summary>A card's text in the app's language once translated; else as the site has it.</summary>
    internal string CardText(string text) =>
        NexusBrowseViewModel.TranslationLanguage is { } language && cardTexts.TryGetValue(language + "\n" + text, out var done) ? done : text;

    /// <summary>The Browse page's preview, for an installed mod's card.</summary>
    internal void PreviewNexusMod(NexusListedMod card)
    {
        if (Browse is not { } page)
            return;
        var item = new NexusBrowseItemViewModel(page, card);
        item.Translate(CardText(card.Name), CardText(card.Summary), "");
        item.UpdateState();
        page.OpenPreview(item);
    }

    /// <summary>The rows are built again, as only the cards load their pictures.</summary>
    private void SetModsCompact(bool compact)
    {
        if (settings.ModsCompact == compact)
            return;
        settings.ModsCompact = compact;
        SaveSettings();
        RefreshMods();
    }

    private Dictionary<string, NexusListedMod> LoadNexusCards()
    {
        try
        {
            if (JsonFile.Read<Dictionary<string, NexusListedMod>>(paths.NexusCardsFile) is { } saved)
                return new Dictionary<string, NexusListedMod>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Saved Nexus Mods cards could not be read, so they come from the site again: {ex.Message}");
        }
        return new Dictionary<string, NexusListedMod>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Keeps the cards the update check read; a mod no longer marked as safe loses its card.</summary>
    private void KeepNexusCards(IReadOnlyList<NexusModInfo> infos)
    {
        if (infos.Count == 0)
            return;
        foreach (var info in infos)
        {
            if (info.Card is { } card)
                NexusCards[info.Ref.CatalogId] = card;
            else
                NexusCards.Remove(info.Ref.CatalogId);
        }
        try
        {
            JsonFile.Write(paths.NexusCardsFile, NexusCards);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Nexus Mods cards could not be saved: {ex.Message}");
        }
    }

    /// <summary>The cards' names and summaries in the app's language; they stay as the site has them when the service fails.</summary>
    private async Task TranslateCardsAsync()
    {
        if (NexusBrowseViewModel.TranslationLanguage is not { } language)
            return;
        var texts = NexusCards.Values.SelectMany(c => new[] { c.Name, c.Summary })
            .Where(t => t.Length > 0 && !cardTexts.ContainsKey(language + "\n" + t)).Distinct(StringComparer.Ordinal).ToList();
        if (texts.Count == 0)
            return;
        try
        {
            var done = await Translator.TranslateAsync(texts, language);
            for (var i = 0; i < texts.Count; i++)
                cardTexts[language + "\n" + texts[i]] = done[i];
        }
        catch (ModException ex)
        {
            Log.Warn(ex.Message);
            return;
        }
        RefreshMods();
    }

    /// <summary>
    /// The name a mod from Nexus Mods gets: the site's name in the app's language, unless the player gave the installed
    /// mod a name of their own, which stays. When the translation service fails, the site's name.
    /// </summary>
    private async Task<string> NexusModNameAsync(NexusModInfo info, ModRecord? installed)
    {
        if (installed is not null && !IsSiteName(installed.Name, info))
            return installed.Name;
        if (NexusBrowseViewModel.TranslationLanguage is not { } language)
            return installed?.Name ?? info.Name;
        try
        {
            using var wait = new CancellationTokenSource(NameTranslationWait);
            var name = (await Translator.TranslateAsync([info.Card?.Name ?? info.Name], language, wait.Token))[0].Trim();
            return name.Length > 0 ? name : installed?.Name ?? info.Name;
        }
        catch (Exception ex) when (ex is ModException or OperationCanceledException)
        {
            Log.Warn($"The name of {info.Ref.PageUrl} stays as the site has it: {ex.Message}");
            return installed?.Name ?? info.Name;
        }
    }

    /// <summary>The name is the one the site gives, in English or in the app's language, and not one the player chose.</summary>
    private bool IsSiteName(string name, NexusModInfo info) =>
        NexusMods.SameName(name, info.Name)
        || info.Card is { } card && (NexusMods.SameName(name, card.Name) || NexusMods.SameName(name, CardText(card.Name)));

    /// <summary>The installed mods that still have the site's name take it in the app's language; a name the player chose stays.</summary>
    private async Task NameInAppLanguageAsync(IReadOnlyList<(ModRecord Mod, NexusModInfo Info)> mods)
    {
        if (NexusBrowseViewModel.TranslationLanguage is null || manager is not { } current)
            return;
        var renamed = 0;
        foreach (var (mod, info) in mods)
        {
            if (!IsSiteName(mod.Name, info))
                continue;
            var name = await NexusModNameAsync(info, mod);
            if (name == mod.Name)
                continue;
            try
            {
                await Task.Run(() => current.Rename(mod.Id, name, mod.Version));
                renamed++;
            }
            catch (Exception ex) when (ex is ModException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"\"{mod.Name}\" keeps its name: {ex.Message}");
            }
        }
        if (renamed > 0)
            RefreshMods();
    }
}
