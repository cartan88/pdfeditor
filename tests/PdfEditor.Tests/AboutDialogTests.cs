using System.Windows.Controls;
using PdfEditor.Dialogs;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;

namespace PdfEditor.Tests;

/// <summary>The About dialog, built without being shown.</summary>
public class AboutDialogTests
{
    private static string Text(AboutDialog dlg, string name) => ((TextBlock)dlg.FindName(name)).Text;
    private static string Button(AboutDialog dlg, string name) => (string)((Button)dlg.FindName(name)).Content;

    [Fact]
    public Task ShowsTheVersionAndComponents() => Ui.Run(() =>
    {
        var dlg = new AboutDialog(installed: true, autoCheck: true, readyVersion: null);
        Assert.Equal($"Version {AppUpdater.CurrentVersion}", Text(dlg, "VersionText"));
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppUpdater.CurrentVersion); // PdfEditor's own version, not the test runner's
        Assert.StartsWith("PDFsharp 6.", Text(dlg, "PdfSharpText"));
        Assert.StartsWith("Velopack ", Text(dlg, "VelopackText"));
        Assert.NotNull(((System.Windows.Controls.Image)dlg.FindName("AppIcon")).Source);

        var details = dlg.DetailsText();
        Assert.Contains($"PDF Editor {AppUpdater.CurrentVersion} (installed)", details);
        Assert.Contains("Windows", details);
        Assert.Contains(".NET", details);
    });

    [Theory]
    [InlineData(true, true, null, "Updates are automatic", "Check now", AboutDialog.Action.CheckForUpdates)]
    [InlineData(true, true, "9.9.9", "Version 9.9.9 is ready", "Restart now", AboutDialog.Action.RestartToUpdate)]
    [InlineData(true, false, null, "Automatic update checks are off", "Check now", AboutDialog.Action.CheckForUpdates)]
    [InlineData(false, true, null, "This copy doesn't update itself", "Get the installer", AboutDialog.Action.None)]
    public Task UpdateCardMatchesTheUpdateState(bool installed, bool autoCheck, string? ready, string title, string button, AboutDialog.Action action) => Ui.Run(() =>
    {
        var dlg = new AboutDialog(installed, autoCheck, ready);
        Assert.Equal(title, Text(dlg, "UpdateTitle"));
        Assert.Equal(button, Button(dlg, "UpdateAction"));
        Assert.Equal(action, (AboutDialog.Action)typeof(AboutDialog).GetField("_cardAction",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(dlg)!);
        Assert.Equal(AboutDialog.Action.None, dlg.RequestedAction); // nothing happens until a button is clicked
    });
}
