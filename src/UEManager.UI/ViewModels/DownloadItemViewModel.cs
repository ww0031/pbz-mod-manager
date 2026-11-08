using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEManager.UI.Services;

namespace UEManager.UI.ViewModels;

/// <summary>A download running in the background: its progress, and a way to stop it.</summary>
/// <param name="size">The file's size in bytes for the figures shown; 0 when not known.</param>
public sealed partial class DownloadItemViewModel(string id, string name, long size) : ObservableObject
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public string Id => id;

    public string Name => name;

    public CancellationToken Token => cancellation.Token;

    [ObservableProperty]
    private double share;

    [ObservableProperty]
    private string detail = "0%";

    public void Report(double value)
    {
        Share = value;
        if (size <= 0)
        {
            Detail = $"{value:P0}";
            return;
        }
        var done = (long)(value * size);
        var speed = (long)(done / Math.Max(clock.Elapsed.TotalSeconds, 0.001));
        Detail = $"{value:P0}  ·  {Format.Size(done)} / {Format.Size(size)}  ·  {Format.Size(speed)}/s";
    }

    [RelayCommand]
    private void Cancel() => cancellation.Cancel();
}
