using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UEManager.Core.Storage;
using UEManager.UI.Services;

namespace UEManager.UI;

/// <summary>
/// The app's start (StartupObject in the csproj). It first checks the .NET Desktop Runtime: with one of its files
/// missing, such as PresentationCore.dll, the app would end before any of its own code runs, without a word. So
/// nothing here touches WPF but the two methods that load it, and the player is told in a Windows message box.
/// </summary>
public static class Program
{
    /// <summary>Microsoft's installer of the newest .NET 8 Desktop Runtime; on a PC that has that version, it offers Repair.</summary>
    private const string RuntimeInstallerUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

    private const uint YesNo = 0x4;
    private const uint IconError = 0x10;
    private const int Yes = 6;

    [STAThread]
    public static void Main()
    {
        try
        {
            LoadWpf();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException
                                       or TypeLoadException or TypeInitializationException)
        {
            ReportBrokenRuntime(ex.GetBaseException().Message);
            return;
        }
        RunApp();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadWpf() => RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp() => App.Main();

    private static void ReportBrokenRuntime(string details)
    {
        // No language is set yet: the one saved in the settings, else the language of Windows.
        L.Current.SetLanguage(AppSettings.Load(AppPaths.NextToApp()).Language ?? L.Current.Offer()?.Code);
        var answer = MessageBoxW(IntPtr.Zero,
            L.F($"{AppInfo.Name} cannot open: the .NET 8 runtime on this PC is damaged or missing files.\n\nClick Yes to download Microsoft's installer for it. Run it and choose Repair (or Install when there is no Repair), then open the app again.\n\nDetails: {details}"),
            AppInfo.Name, YesNo | IconError);
        if (answer == Yes)
            Shell.Open(RuntimeInstallerUrl);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
