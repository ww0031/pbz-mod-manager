using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UEManager.Core;
using UEManager.Core.Mods;
using UEManager.UI.Services;

namespace UEManager.UI.ViewModels;

/// <summary>
/// Nexus Mods: a mod installed from its link, the update check for the mods that came from there, and Mod Manager
/// Download links (nxm://) from the site. Reading a mod needs no account; a download needs the player's API key.
/// </summary>
public sealed partial class MainViewModel
{
    private const string NxmKey = @"Software\Classes\nxm";

    private sealed record NexusUpdate(NexusModInfo Info, NexusFile File);

    private enum NexusStart { Started, OnSite, Stopped }

    /// <summary>The newest file per mod id, for the mods with an update; filled by the update check.</summary>
    private readonly Dictionary<string, NexusUpdate> nexusUpdates = [];

    private string? NexusGame => NexusMods.GameOf(Profile.ModPageUrl);

    /// <summary>The mod link the player pasted on the Mods page.</summary>
    [ObservableProperty]
    private string nexusLink = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckNexusUpdates))]
    private bool isCheckingNexusUpdates;

    public bool CanCheckNexusUpdates => !IsCheckingNexusUpdates;

    public int NexusUpdateCount => manager is null ? 0 : OwnMods.Count(m => nexusUpdates.ContainsKey(m.Id));

    public bool HasNexusUpdates => NexusUpdateCount > 0;

    public string UpdateAllText => L.F($"Update all ({NexusUpdateCount})");

    /// <summary>The player's own key from their Nexus Mods account, saved with the settings.</summary>
    public string NexusApiKey
    {
        get => settings.NexusApiKey ?? "";
        set
        {
            var key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (key == settings.NexusApiKey)
                return;
            settings.NexusApiKey = key;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The update for a mod's row, such as "Update to 3"; null when it has none.</summary>
    internal string? NexusUpdateText(ModRecord mod)
    {
        if (!nexusUpdates.TryGetValue(mod.Id, out var update))
            return null;
        var file = update.File;
        return ModNaming.SiteFile(mod.SourceFileName) is { } site && NexusMods.SameName(site.Name, file.Name)
            ? L.F($"Update to {file.Version}")
            : L.F($"Update to {file.Name} {file.Version}");
    }

    /// <summary>Installs the mod of the pasted link, or updates it when it is installed already.</summary>
    [RelayCommand]
    private async Task ImportNexusLink()
    {
        if (!EnsureGame())
            return;
        if (NexusMods.ParseUrl(NexusLink) is not { } mod)
        {
            ShowToast(L.T("Paste the link of a mod on Nexus Mods, such as https://www.nexusmods.com/phantombladezero/mods/260."), isError: true);
            return;
        }
        if (!IsThisGame(mod))
            return;
        await ImportNexusModAsync(mod);
    }

    /// <summary>Mods Menu on Nexus Mods; Phantom Blade Zero's script mods may run in it.</summary>
    private const int ModsMenuModId = 149;

    /// <summary>Installs the script loader a mod needs, from the install review: Mods Menu from Nexus Mods, PBZModLoader from its card.</summary>
    private void InstallLoader(string loader)
    {
        ShowToast(L.F($"Installing {loader}. Once it is in, install this mod again."));
        if (loader == PackageScanner.ForModLoader
            && RecommendedMain.Mod?.Repo?.Contains("pbzmodloader", StringComparison.OrdinalIgnoreCase) == true)
            _ = RecommendedMain.InstallCommand.ExecuteAsync(null);
        else if (loader == PackageScanner.ForModsMenu && NexusGame is { } game)
            _ = ImportNexusModAsync(new NexusModRef(game, ModsMenuModId));
    }

    private async Task ImportNexusModAsync(NexusModRef mod)
    {
        NexusModInfo? info = null;
        if (!await RunAsync(L.T("Reading the mod on Nexus Mods…"), async () => info = await NexusMods.GetModAsync(Http, mod)))
            return;

        var installed = OwnMods.Where(m => NexusMods.RefOf(m, NexusGame) == mod).ToList();
        if (installed.Count > 0)
        {
            var update = installed.Select(m => (Mod: m, File: NexusMods.FindUpdate(info!.Files, m.SourceFileName, m.Version)))
                .FirstOrDefault(found => found.File is not null);
            if (update.File is null)
            {
                ShowToast(L.F($"\"{installed[0].Name}\" is installed and up to date."));
                NexusLink = "";
                return;
            }
            if (await InstallNexusFileAsync(info!, update.File, update.Mod, nxm: null) == NexusStart.Started)
                NexusLink = "";
            return;
        }
        if (NexusMods.PickFile(info!.Files, null) is not { } file)
        {
            ShowToast(L.F($"\"{info.Name}\" has no file to download on Nexus Mods."), isError: true);
            return;
        }
        if (await InstallNexusFileAsync(info, file, installed: null, nxm: null) == NexusStart.Started)
            NexusLink = "";
    }

    /// <summary>A Mod Manager Download link from the site, handed over by the browser.</summary>
    private async Task InstallNxmAsync(string text)
    {
        if (NexusMods.ParseNxm(text) is not { } link)
        {
            ShowToast(L.T("This Mod Manager Download link is incomplete. Click Mod Manager Download on the site again."), isError: true);
            return;
        }
        if (!EnsureGame() || !IsThisGame(link.Mod))
            return;
        NexusModInfo? info = null;
        if (!await RunAsync(L.T("Reading the mod on Nexus Mods…"), async () => info = await NexusMods.GetModAsync(Http, link.Mod)))
            return;
        if (info!.Files.FirstOrDefault(f => f.FileId == link.FileId) is not { } file)
        {
            ShowToast(L.T("Nexus Mods no longer has this file."), isError: true);
            return;
        }
        await InstallNexusFileAsync(info, file, InstalledFrom(info, file), link);
    }

    /// <summary>
    /// The installed mod a file of a Nexus Mods mod updates: the one installed from a file of the same name, or the only
    /// one from that mod when the author no longer offers its file. Null installs the file as a mod of its own.
    /// </summary>
    private ModRecord? InstalledFrom(NexusModInfo info, NexusFile file)
    {
        var fromMod = OwnMods.Where(m => NexusMods.RefOf(m, NexusGame) == info.Ref).ToList();
        return fromMod.FirstOrDefault(m => ModNaming.SiteFile(m.SourceFileName) is { } site && NexusMods.SameName(site.Name, file.Name))
            ?? (fromMod is [var only] && ModNaming.SiteFile(only.SourceFileName) is { } s
                && !info.Files.Any(f => f.IsCurrent && NexusMods.SameName(f.Name, s.Name)) ? only : null);
    }

    private bool IsThisGame(NexusModRef mod)
    {
        if (NexusGame is null || string.Equals(mod.Game, NexusGame, StringComparison.OrdinalIgnoreCase))
            return true;
        ShowToast(L.F($"This link is for another game on Nexus Mods ({mod.Game}), not {GameDisplayName}."), isError: true);
        return false;
    }

    /// <summary>
    /// Downloads a file of a Nexus Mods mod in the background and installs it. A new mod takes the mod's name on the
    /// site, which the player can change in its details; an update keeps the installed mod's name.
    /// </summary>
    private async Task<NexusStart> InstallNexusFileAsync(NexusModInfo info, NexusFile file, ModRecord? installed, NxmLink? nxm)
    {
        // Still here when the player cancelled the review or the install failed: the site is not asked again.
        var kept = KeptDownload(NexusMods.DownloadFileName(info.Ref, file, ""));
        string? url = null;
        if (kept is null)
        {
            if (string.IsNullOrWhiteSpace(settings.NexusApiKey))
            {
                ShowToast(L.T("Downloads from Nexus Mods need your API key. Enter it in Settings ▸ Nexus Mods."), isError: true,
                    L.T("Open Settings"), () => SelectedPage = "Settings");
                return NexusStart.Stopped;
            }
            var onSite = false;
            await RunAsync(L.T("Asking Nexus Mods for the download…"), async () =>
            {
                try
                {
                    url = await NexusMods.DownloadUrlAsync(Http, settings.NexusApiKey, info.Ref, file.FileId, nxm);
                }
                catch (NexusPremiumRequiredException)
                {
                    onSite = true;
                }
            });
            if (onSite)
            {
                await DownloadOnSiteAsync(info.Ref, file);
                return NexusStart.OnSite;
            }
            if (url is null)
                return NexusStart.Stopped;
        }

        // Each installed file of the mod has a mark of its own, so an update replaces that one and no other.
        var catalogId = installed?.CatalogId
            ?? (OwnMods.Any(m => m != installed && m.CatalogId == info.Ref.CatalogId) ? $"{info.Ref.CatalogId}/f{file.FileId}" : info.Ref.CatalogId);
        if (installed is not null && installed.CatalogId is null
            && !await RunAsync(L.T("Saving…"), () => Task.Run(() => manager!.Link(installed.Id, catalogId))))
            return NexusStart.Stopped;

        var extension = kept is not null ? Path.GetExtension(kept)
            : Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : "");
        if (!ArchiveStager.IsSupported("download" + extension))
            extension = ".zip";
        await InstallFromReleaseAsync(catalogId, installed?.Name ?? info.Name, file.Version, url is null ? [] : [url],
            NexusMods.DownloadFileName(info.Ref, file, extension), 0, null, shownSize: file.Size);
        if (installed is not null && manager!.Mods.All(m => m.Id != installed.Id))
        {
            nexusUpdates.Remove(installed.Id);
            RefreshMods();
        }
        return NexusStart.Started;
    }

    /// <summary>
    /// Without Premium, Nexus Mods starts a download only on its own page. Its Mod Manager Download button then hands the
    /// file to the app that opens nxm:// links, which the app becomes first, when the player agrees.
    /// </summary>
    private async Task DownloadOnSiteAsync(NexusModRef mod, NexusFile file)
    {
        var handler = IsNxmHandler;
        var message = L.Join(L.T("Nexus Mods gives direct downloads only to Premium members. Without Premium, a download starts on the mod's page."),
            handler
                ? L.T("Click Mod Manager Download on the page that opens. The app then downloads and installs the file.")
                : L.T("Click Mod Manager Download on the page that opens. So that the button hands the file to this app, the app first becomes the one that opens Mod Manager Download links (nxm://), in place of Vortex or another manager."));
        if (!await AskAsync(L.T("Download on Nexus Mods"), message, handler ? L.T("Open the page") : L.T("Use this app and open the page")))
            return;
        if (!handler)
            RegisterNxm();
        OpenPath(mod.FilePageUrl(file.FileId));
    }

    [RelayCommand]
    private Task CheckNexusUpdates() => CheckNexusUpdatesAsync(quiet: false);

    /// <summary>Looks up every mod that came from Nexus Mods there; no account needed.</summary>
    /// <param name="quiet">At start: says something only when there are updates.</param>
    internal async Task CheckNexusUpdatesAsync(bool quiet)
    {
        if (manager is null || IsCheckingNexusUpdates)
            return;
        var mods = OwnMods.Select(m => (Mod: m, Ref: NexusMods.RefOf(m, NexusGame))).Where(found => found.Ref is not null).ToList();
        if (mods.Count == 0)
        {
            if (!quiet)
                ShowToast(L.T("None of your mods is known to come from Nexus Mods: install one from its link, or from a download that kept the site's file name."));
            return;
        }
        IsCheckingNexusUpdates = true;
        var failed = 0;
        try
        {
            nexusUpdates.Clear();
            foreach (var group in mods.GroupBy(found => found.Ref!))
            {
                try
                {
                    var info = await NexusMods.GetModAsync(Http, group.Key);
                    foreach (var (mod, _) in group)
                    {
                        if (NexusMods.FindUpdate(info.Files, mod.SourceFileName, mod.Version) is { } file)
                            nexusUpdates[mod.Id] = new NexusUpdate(info, file);
                    }
                }
                catch (ModException ex)
                {
                    failed++;
                    Log.Warn($"Update check for {group.Key.PageUrl} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            IsCheckingNexusUpdates = false;
        }
        Log.Info($"Nexus Mods update check: {mods.Count} mod(s), {nexusUpdates.Count} update(s), {failed} failed.");
        RefreshMods();
        var count = NexusUpdateCount;
        if (count > 0)
            ShowToast(L.Plural(count, "{0} mod has an update on Nexus Mods.", "{0} mods have an update on Nexus Mods."));
        else if (failed > 0 && !quiet)
            ShowToast(L.T("Nexus Mods could not be reached for some mods. See the log."), isError: true);
        else if (!quiet)
            ShowToast(L.T("Every mod from Nexus Mods is up to date."));
    }

    internal async Task UpdateNexusModAsync(ModRecord mod)
    {
        if (nexusUpdates.TryGetValue(mod.Id, out var update))
            await InstallNexusFileAsync(update.Info, update.File, mod, nxm: null);
    }

    [RelayCommand]
    private async Task UpdateAllNexusMods()
    {
        foreach (var mod in OwnMods.Where(m => nexusUpdates.ContainsKey(m.Id)).ToList())
        {
            if (IsBusy || InstallDialog is not null || await InstallNexusFileAsync(nexusUpdates[mod.Id].Info, nexusUpdates[mod.Id].File, mod, null) != NexusStart.Started)
                break;
        }
    }

    [RelayCommand]
    private void OpenNexusApiKeyPage() => OpenPath(NexusMods.ApiKeyPage);

    [RelayCommand]
    private async Task CheckNexusApiKey()
    {
        if (string.IsNullOrWhiteSpace(settings.NexusApiKey))
        {
            ShowToast(L.T("Enter your Nexus Mods API key first."), isError: true);
            return;
        }
        (string Name, bool Premium) account = default;
        if (await RunAsync(L.T("Checking the API key…"), async () => account = await NexusMods.ValidateAsync(Http, settings.NexusApiKey)))
            ShowToast(account.Premium
                ? L.F($"The key works: {account.Name}, Premium. Mod links download and install directly.")
                : L.F($"The key works: {account.Name}, without Premium. A download starts on the mod's page with Mod Manager Download."));
    }

    private static string? NxmCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(NxmKey + @"\shell\open\command");
            return key?.GetValue("") as string;
        }
    }

    /// <summary>This exe opens Mod Manager Download links (nxm://) from the site.</summary>
    public bool IsNxmHandler => Environment.ProcessPath is { } exe && NxmCommand?.Contains(exe, StringComparison.OrdinalIgnoreCase) == true;

    public string NxmHandlerText => IsNxmHandler ? L.T("This app opens Mod Manager Download links.")
        : NxmCommand is null ? L.T("No app opens Mod Manager Download links yet.")
        : L.T("Another app, such as Vortex, opens Mod Manager Download links now.");

    /// <summary>Makes this exe the one that opens nxm:// links, for the current Windows user only.</summary>
    [RelayCommand]
    private void RegisterNxm()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(NxmKey);
            key.SetValue("", "URL:NXM Protocol");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
            Log.Info($"Registered '{exe}' for nxm:// links.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn($"nxm:// links could not be registered: {ex.Message}");
            ShowToast(L.F($"Something went wrong: {ex.Message}"), isError: true);
        }
        OnPropertyChanged(nameof(IsNxmHandler));
        OnPropertyChanged(nameof(NxmHandlerText));
    }
}
