using Velopack;

namespace PdfEditor;

/// <summary>Entry point. Velopack must run first: the installer and updater start the app with special arguments.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Handles install, update and uninstall hooks (and exits for those), and applies a downloaded update
        // that wasn't applied when the app last closed.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
