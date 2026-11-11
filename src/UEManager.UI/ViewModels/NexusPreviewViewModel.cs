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

/// <summary>A line of a mod's description in its preview.</summary>
public sealed record NexusPreviewBlock(string Text, NexusBlockKind Kind)
{
    public bool IsHeading => Kind == NexusBlockKind.Heading;

    public bool IsItem => Kind == NexusBlockKind.Item;
}

/// <summary>
/// A mod's preview from the Browse page: its picture, what changed in its newest versions and its description in the
/// app's language, and the pictures of the description. The pictures are kept by the preview alone, so they go when it closes.
/// </summary>
public sealed partial class NexusPreviewViewModel(NexusBrowseViewModel page, NexusBrowseItemViewModel item, HttpClient http)
    : LocalizedObservableObject
{
    private const int MaxPictures = 8;
    private const int ChangedVersions = 3;

    private readonly RemoteImages images = new(http);
    private NexusDescription? description;
    private IReadOnlyList<NexusChange> changes = [];
    private IReadOnlyList<string>? translated;
    private IReadOnlyList<string>? translatedChanges;

    public NexusBrowseViewModel Page => page;

    public NexusBrowseItemViewModel Item => item;

    public ObservableCollection<NexusPreviewBlock> Blocks { get; } = [];

    /// <summary>The changelog of the newest versions: a heading per version, then its lines.</summary>
    public ObservableCollection<NexusPreviewBlock> Changes { get; } = [];

    public bool HasChanges => Changes.Count > 0;

    public ObservableCollection<ImageSource> Pictures { get; } = [];

    [ObservableProperty]
    private ImageSource? picture;

    [ObservableProperty]
    private bool isLoading = true;

    [ObservableProperty]
    private string? error;

    /// <summary>The translation is on its way, or failed.</summary>
    [ObservableProperty]
    private string? note;

    public async Task LoadAsync()
    {
        _ = LoadPictureAsync();
        try
        {
            var text = await NexusMods.DescriptionAsync(http, new NexusModRef(page.Game, item.ModId));
            if (text is null)
            {
                Error = L.T("This mod is not marked as safe for all ages, so it is not shown.");
                return;
            }
            description = NexusText.Parse(text);
            changes = await LoadChangesAsync();
            Show();
            _ = LoadPicturesAsync(description.Images);
        }
        catch (ModException ex)
        {
            Log.Warn(ex.Message);
            Error = L.Message(ex);
            return;
        }
        finally
        {
            IsLoading = false;
        }
        await TranslateAsync();
    }

    /// <summary>Shows the description again, translated or as its author wrote it.</summary>
    internal void Refresh() => Show();

    [RelayCommand]
    private void Close() => page.ClosePreview();

    /// <summary>The preview closes first, so the download's progress and any question about the install are in sight.</summary>
    [RelayCommand]
    private Task Install()
    {
        page.ClosePreview();
        return item.InstallCommand.ExecuteAsync(null);
    }

    private async Task TranslateAsync()
    {
        if (NexusBrowseViewModel.TranslationLanguage is not { } language || description is null)
            return;
        var texts = description.Blocks.Select(b => b.Text).Concat(changes.SelectMany(c => c.Lines)).ToList();
        if (texts.Count == 0)
            return;
        Note = L.T("Translating the description…");
        try
        {
            var all = await page.TranslateAsync(texts, language);
            translated = all.Take(description.Blocks.Count).ToList();
            translatedChanges = all.Skip(description.Blocks.Count).ToList();
            Note = null;
            Show();
        }
        catch (ModException ex)
        {
            Log.Warn(ex.Message);
            Note = L.T("The free translation service cannot be reached right now, so the texts show as their authors wrote them.");
        }
    }

    private void Show()
    {
        if (description is null)
            return;
        Blocks.Clear();
        for (var i = 0; i < description.Blocks.Count; i++)
        {
            var block = description.Blocks[i];
            Blocks.Add(new NexusPreviewBlock(!page.ShowOriginal && translated is not null ? translated[i] : block.Text, block.Kind));
        }

        Changes.Clear();
        var line = 0;
        foreach (var change in changes)
        {
            var date = change.Date.LocalDateTime.ToString(L.T("d MMM yyyy"), CultureInfo.CurrentCulture);
            Changes.Add(new NexusPreviewBlock($"{L.F($"Version {change.Version}")}  ·  {date}", NexusBlockKind.Heading));
            foreach (var text in change.Lines)
                Changes.Add(new NexusPreviewBlock(!page.ShowOriginal && translatedChanges is not null ? translatedChanges[line++] : text, NexusBlockKind.Item));
        }
        OnPropertyChanged(nameof(HasChanges));
    }

    /// <summary>The preview shows without the changelog when the site cannot give it.</summary>
    private async Task<IReadOnlyList<NexusChange>> LoadChangesAsync()
    {
        try
        {
            return await NexusMods.ChangesAsync(http, new NexusModRef(page.Game, item.ModId), ChangedVersions);
        }
        catch (ModException ex)
        {
            Log.Warn(ex.Message);
            return [];
        }
    }

    private async Task LoadPictureAsync() => Picture = await images.LoadAsync(item.Mod.Picture ?? item.Mod.Thumbnail, 960);

    private async Task LoadPicturesAsync(IReadOnlyList<string> urls)
    {
        foreach (var url in urls.Where(u => u != item.Mod.Picture).Take(MaxPictures))
        {
            if (await images.LoadAsync(url, 860) is { } image)
                Pictures.Add(image);
        }
    }
}

