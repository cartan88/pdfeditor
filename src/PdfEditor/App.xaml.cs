using System.Windows;
using System.Windows.Threading;

namespace PdfEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0)
            _ = window.OpenAsync(e.Args[0]);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, "Something went wrong:\n\n" + e.Exception.Message, "PDF Editor",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
