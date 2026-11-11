using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEManager.Core;
using UEManager.Core.Mods;
using UEManager.UI.Catalog;

namespace UEManager.UI.ViewModels;

/// <summary>
/// The Browse page: the game's mods on Nexus Mods, a page at a time, with their names, summaries and descriptions
/// translated for free into the app's language. Adult content is never shown (see <see cref="NexusMods.ListModsAsync"/>).
/// </summary>
public sealed partial class NexusBrowseViewModel : LocalizedObservableObject
{
    private const int PageSize = 20;

    private readonly HttpClient http;
    private readonly FreeTranslator translator;
    private NexusSort sort = NexusSort.Endorsements;
    private int nextOffset;
    private int loadId;
    private bool loaded;

    public NexusBrowseViewModel(MainViewModel owner, HttpClient http, FreeTranslator translator, string game)
    {
        Owner = owner;
        this.http = http;
        this.translator = translator;
        Game = game;
        Images = new RemoteImages(http);
        UpdateSortOptions();
    }

    internal MainViewModel Owner { get; }

    /// <summary>The game's short name on Nexus Mods, such as "phantombladezero".</summary>
    internal string Game { get; }

    internal RemoteImages Images { get; }

    public ObservableCollection<NexusBrowseItemViewModel> Items { get; } = [];

    /// <summary>The order buttons above the list.</summary>
    [ObservableProperty]
    private IReadOnlyList<TagFilterItem> sortOptions = [];

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore), nameof(ShowEmpty))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore), nameof(CountText))]
    private int total;

    /// <summary>The texts as their authors wrote them, instead of translated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslationButtonText))]
    private bool showOriginal;

    /// <summary>Set when the translation service failed, so the texts show untranslated.</summary>
    [ObservableProperty]
    private string? translationNote;

    /// <summary>What the list was searched for, when the player's words went to the site in English.</summary>
    [ObservableProperty]
    private string? searchNote;

    [ObservableProperty]
    private NexusPreviewViewModel? preview;

    /// <summary>The app's language for the translations; null in English, which needs none.</summary>
    internal static string? TranslationLanguage => L.Current.Language == Localizer.English ? null : L.Current.Language;

    public bool CanTranslate => TranslationLanguage is not null;

    public string TranslationButtonText => ShowOriginal ? L.T("Show translation") : L.T("Show original");

    public bool HasMore => !IsLoading && nextOffset < Total;

    public string CountText => L.F($"{Items.Count} of {Total} shown");

    public bool ShowEmpty => loaded && !IsLoading && Error is null && Items.Count == 0;

    /// <summary>Reads the first page when the page opens for the first time.</summary>
    public Task LoadAsync() => loaded || IsLoading ? Task.CompletedTask : LoadPageAsync(reset: true);

    internal Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string language) =>
        translator.TranslateAsync(texts, language);

    /// <summary>Updates every card after the installed mods or their updates changed.</summary>
    public void UpdateInstalled()
    {
        foreach (var item in Items)
            item.UpdateState();
    }

    internal void OpenPreview(NexusBrowseItemViewModel item)
    {
        Preview = new NexusPreviewViewModel(this, item, http);
        _ = Preview.LoadAsync();
    }

    internal void ClosePreview() => Preview = null;

    protected override void RefreshLanguage()
    {
        UpdateSortOptions();
        base.RefreshLanguage();
    }

    [RelayCommand]
    private Task Search() => LoadPageAsync(reset: true);

    [RelayCommand]
    private Task LoadMore() => LoadPageAsync(reset: false);

    [RelayCommand]
    private void OpenSite() => Owner.OpenPath($"https://www.nexusmods.com/{Game}/mods/");

    [RelayCommand]
    private void ToggleOriginal() => ShowOriginal = !ShowOriginal;

    partial void OnShowOriginalChanged(bool value)
    {
        foreach (var item in Items)
            item.Refresh();
        Preview?.Refresh();
    }

    private void SortBy(NexusSort value)
    {
        if (value == sort)
            return;
        sort = value;
        UpdateSortOptions();
        _ = LoadPageAsync(reset: true);
    }

    private void UpdateSortOptions() => SortOptions =
    [
        Option(L.T("Most endorsed"), NexusSort.Endorsements),
        Option(L.T("Most downloaded"), NexusSort.Downloads),
        Option(L.T("Recently updated"), NexusSort.Updated),
        Option(L.T("Newest"), NexusSort.Created),
    ];

    private TagFilterItem Option(string label, NexusSort value) => new(label, value == sort, new RelayCommand(() => SortBy(value)));

    /// <summary>The first page again, or the next one. A newer load makes an older one's answer count for nothing.</summary>
    private async Task LoadPageAsync(bool reset)
    {
        var id = ++loadId;
        IsLoading = true;
        Error = null;
        if (reset)
        {
            Items.Clear();
            nextOffset = 0;
            OnPropertyChanged(nameof(CountText));
        }
        try
        {
            var offset = nextOffset;
            var search = await SearchTermAsync(SearchText.Trim());
            var page = await NexusMods.ListModsAsync(http, Game, sort, search, offset, PageSize);
            var items = page.Mods.Where(m => Items.All(i => i.ModId != m.ModId)).Select(m => new NexusBrowseItemViewModel(this, m)).ToList();
            await TranslateItemsAsync(items);
            if (id != loadId)
                return;
            loaded = true;
            SearchNote = search != SearchText.Trim() ? L.F($"Searched in English for \"{search}\"") : null;
            nextOffset = offset + PageSize;
            Total = page.Total;
            foreach (var item in items)
            {
                item.UpdateState();
                Items.Add(item);
            }
            OnPropertyChanged(nameof(CountText));
            _ = Task.WhenAll(items.Select(async item => item.Cover = await Images.LoadAsync(item.Mod.Thumbnail ?? item.Mod.Picture, 400)));
        }
        catch (ModException ex)
        {
            if (id != loadId)
                return;
            Log.Warn(ex.Message);
            Error = L.Message(ex);
        }
        finally
        {
            if (id == loadId)
                IsLoading = false;
        }
    }

    /// <summary>Mod names on the site are mostly English, so a search in other words goes there in English.</summary>
    private async Task<string> SearchTermAsync(string text)
    {
        if (text.Length == 0 || !FreeTranslator.NeedsTranslation(text, "en"))
            return text;
        try
        {
            return (await translator.TranslateAsync([text], "en"))[0].Trim();
        }
        catch (ModException ex)
        {
            Log.Warn(ex.Message);
            return text;
        }
    }

    /// <summary>The names, summaries and categories in the app's language; they stay as they are when the service fails.</summary>
    private async Task TranslateItemsAsync(IReadOnlyList<NexusBrowseItemViewModel> items)
    {
        if (TranslationLanguage is not { } language || items.Count == 0)
            return;
        try
        {
            var texts = await translator.TranslateAsync(items.SelectMany(i => new[] { i.Mod.Name, i.Mod.Summary, i.Mod.Category ?? "" }).ToList(), language);
            for (var i = 0; i < items.Count; i++)
                items[i].Translate(texts[3 * i], texts[3 * i + 1], texts[3 * i + 2]);
            TranslationNote = null;
        }
        catch (ModException ex)
        {
            Log.Warn(ex.Message);
            TranslationNote = L.T("The free translation service cannot be reached right now, so the texts show as their authors wrote them.");
        }
    }
}

/// <summary>One mod on the Browse page.</summary>
public sealed partial class NexusBrowseItemViewModel(NexusBrowseViewModel page, NexusListedMod mod) : LocalizedObservableObject
{
    private string? translatedName;
    private string? translatedSummary;
    private string? translatedCategory;
    private ModRecord? installed;
    private bool hasUpdate;

    public NexusListedMod Mod => mod;

    public int ModId => mod.ModId;

    public string Name => !page.ShowOriginal && translatedName is { Length: > 0 } name ? name : mod.Name;

    /// <summary>The name as its author wrote it, under the translated one.</summary>
    public string? OriginalName => Name == mod.Name ? null : mod.Name;

    public string Summary => !page.ShowOriginal && translatedSummary is { Length: > 0 } summary ? summary : mod.Summary;

    public string AuthorText => L.F($"by {mod.Author ?? L.T("an unknown author")}");

    public string MetaText => string.Join("  ·  ", new[]
    {
        mod.Version.Length > 0 ? L.F($"Version {mod.Version}") : null,
        mod.Updated is { } updated ? L.F($"Updated {updated.LocalDateTime.ToString(L.T("d MMM yyyy"), CultureInfo.CurrentCulture)}") : null,
        !page.ShowOriginal && translatedCategory is { Length: > 0 } category ? category : mod.Category,
    }.Where(part => part is not null));

    public string EndorsementsText => mod.Endorsements.ToString("N0", CultureInfo.CurrentCulture);

    public string DownloadsText => mod.Downloads.ToString("N0", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private ImageSource? cover;

    /// <summary>Install, or Update when the update check found a newer file; nothing for a mod that is up to date.</summary>
    public string? ActionText => installed is null ? L.T("Install") : hasUpdate ? L.T("Update") : null;

    public bool HasAction => ActionText is not null;

    public string? StatusText => installed is null ? null
        : installed.Version is null ? L.T("Installed") : L.F($"Installed  ·  {installed.Version}");

    internal void Translate(string name, string summary, string category)
    {
        translatedName = name;
        translatedSummary = summary;
        translatedCategory = category;
        Refresh();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(OriginalName));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(MetaText));
    }

    internal void UpdateState()
    {
        installed = page.Owner.InstalledFromNexus(mod.ModId);
        hasUpdate = installed is not null && page.Owner.HasNexusUpdate(installed);
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(HasAction));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private Task Install() => page.Owner.InstallFromBrowseAsync(mod.ModId);

    [RelayCommand]
    private void Preview() => page.OpenPreview(this);

    [RelayCommand]
    private void OpenPage() => page.Owner.OpenPath(new NexusModRef(page.Game, mod.ModId).PageUrl);
}
