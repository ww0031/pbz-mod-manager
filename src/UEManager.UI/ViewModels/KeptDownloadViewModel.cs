using CommunityToolkit.Mvvm.Input;
using UEManager.Core.Mods;
using UEManager.UI.Services;

namespace UEManager.UI.ViewModels;

/// <summary>A download kept because its install was cancelled or failed: one click installs it without a new download, or deletes it.</summary>
public sealed partial class KeptDownloadViewModel(MainViewModel owner, string path, long size)
{
    public string Path => path;

    public string Title => ModNaming.SiteFile(path) is { } site ? $"{site.Name}  {site.Version}" : System.IO.Path.GetFileNameWithoutExtension(path);

    public string SizeText => Format.Size(size);

    [RelayCommand]
    private Task Install() => owner.InstallKeptAsync(this);

    [RelayCommand]
    private void Delete() => owner.DeleteKept(this);
}
