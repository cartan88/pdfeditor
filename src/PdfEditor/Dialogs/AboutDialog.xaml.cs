using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Win32;
using PdfEditor.Services;

namespace PdfEditor.Dialogs;

/// <summary>Version, update status, system details and links. Update actions are carried out by the caller after closing.</summary>
public partial class AboutDialog : Window
{
    public enum Action { None, CheckForUpdates, RestartToUpdate }

    /// <param name="installed">Installed with the Setup program (so it can update itself).</param>
    /// <param name="autoCheck">The startup update check is turned on.</param>
    /// <param name="readyVersion">A downloaded update waiting for a restart, if any.</param>
    public AboutDialog(bool installed, bool autoCheck, string? readyVersion)
    {
        InitializeComponent();
        AppIcon.Source = LargeIconFrame();
        string version = AppUpdater.CurrentVersion;
        VersionText.Text = $"Version {version}";
        CopyrightText.Text = "© 2026 Charles Tan";
        WindowsText.Text = WindowsDescription();
        DotNetText.Text = RuntimeInformation.FrameworkDescription;
        LocationText.Text = ShortPath(AppContext.BaseDirectory.TrimEnd('\\'));
        LocationText.ToolTip = AppContext.BaseDirectory;
        PdfSharpText.Text = $"PDFsharp {AssemblyVersion(typeof(PdfSharp.Pdf.PdfDocument))} (MIT licence) for reading, filling and saving PDFs";
        VelopackText.Text = $"Velopack {AssemblyVersion(typeof(Velopack.VelopackApp))} (MIT licence) for installing and updating";

        NotesLink.NavigateUri = new Uri(AppUpdater.ReleasesPage);
        IssuesLink.NavigateUri = new Uri(AppUpdater.RepositoryPage + "/issues/new");
        SourceLink.NavigateUri = new Uri(AppUpdater.RepositoryPage);

        if (readyVersion != null)
            ShowUpdateState("⬆", $"Version {readyVersion} is ready", "It has been downloaded. Restart PDF Editor to start using it.",
                "Restart now", Action.RestartToUpdate, Color.FromRgb(0xEF, 0xF6, 0xFF), Color.FromRgb(0xBF, 0xDB, 0xFE), Color.FromRgb(0x25, 0x63, 0xEB));
        else if (installed && autoCheck)
            ShowUpdateState("✓", "Updates are automatic", "New versions download in the background. You'll see a button to restart into them.",
                "Check now", Action.CheckForUpdates, Color.FromRgb(0xF0, 0xFD, 0xF4), Color.FromRgb(0xBB, 0xF7, 0xD0), Color.FromRgb(0x16, 0xA3, 0x4A));
        else if (installed)
            ShowUpdateState("!", "Automatic update checks are off", "Turn them on with Help › Check for updates automatically, or check now.",
                "Check now", Action.CheckForUpdates, Color.FromRgb(0xFF, 0xFB, 0xEB), Color.FromRgb(0xFD, 0xE6, 0x8A), Color.FromRgb(0xB4, 0x53, 0x09));
        else
            ShowUpdateState("i", "This copy doesn't update itself", "It wasn't installed with the installer. Install PDF Editor with PdfEditor-Setup.exe to get new versions automatically.",
                "Get the installer", Action.None, Color.FromRgb(0xF9, 0xFA, 0xFB), Color.FromRgb(0xE5, 0xE7, 0xEB), Color.FromRgb(0x4B, 0x55, 0x63));
        _installed = installed;
    }

    private readonly bool _installed;
    private Action _cardAction;

    /// <summary>What the user asked for; the caller performs it once the dialog has closed.</summary>
    public Action RequestedAction { get; private set; }

    private void ShowUpdateState(string icon, string title, string detail, string button, Action action, Color background, Color border, Color accent)
    {
        UpdateIcon.Text = icon;
        UpdateIcon.Foreground = new SolidColorBrush(accent);
        UpdateIcon.FontWeight = FontWeights.Bold;
        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateAction.Content = button;
        UpdateCard.Background = new SolidColorBrush(background);
        UpdateCard.BorderBrush = new SolidColorBrush(border);
        _cardAction = action;
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (!_installed && _cardAction == Action.None)
        {
            OpenUrl(AppUpdater.ReleasesPage + "/latest");
            return;
        }
        RequestedAction = _cardAction;
        DialogResult = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailsText());
            CopyButton.Content = "Copied ✓";
        }
        catch (Exception)
        {
            CopyButton.Content = "Couldn't copy";
        }
    }

    /// <summary>Plain-text summary for problem reports.</summary>
    public string DetailsText() => string.Join(Environment.NewLine,
        $"PDF Editor {AppUpdater.CurrentVersion} ({(_installed ? "installed" : "portable")})",
        WindowsDescription(),
        RuntimeInformation.FrameworkDescription,
        $"PDFsharp {AssemblyVersion(typeof(PdfSharp.Pdf.PdfDocument))}, Velopack {AssemblyVersion(typeof(Velopack.VelopackApp))}");

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser registered: nothing useful to do */ }
    }

    /// <summary>
    /// The app icon's 128 px frame: an Image given the .ico picks its first (16 px) frame and scales it up blurrily.
    /// 128 px stays sharp at 64 DIP up to 200% display scaling.
    /// </summary>
    private static ImageSource? LargeIconFrame()
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/PdfEditor;component/Assets/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= 128) ?? decoder.Frames.MaxBy(f => f.PixelWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string AssemblyVersion(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? type.Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>For example "Windows 11 25H2 (build 26300), 64-bit".</summary>
    private static string WindowsDescription()
    {
        var v = Environment.OSVersion.Version;
        string name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : v.Major == 10 ? "Windows 10" : $"Windows {v.Major}.{v.Minor}";
        string? release = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            release = key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception) { /* not available */ }
        string bits = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "64-bit",
            Architecture.Arm64 => "ARM64",
            Architecture.X86 => "32-bit",
            var other => other.ToString(),
        };
        return $"{name}{(release != null ? " " + release : "")} (build {v.Build}), {bits}";
    }

    /// <summary>Shortens paths under the user's local app data, where installed copies live.</summary>
    private static string ShortPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + path[local.Length..] : path;
    }
}
