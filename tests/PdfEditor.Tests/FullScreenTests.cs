using System.Reflection;
using System.Windows;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>Full screen (F11): hides the window chrome and restores the window exactly afterwards. The window is never shown.</summary>
public class FullScreenTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Call(MainWindow win, string method) => typeof(MainWindow).GetMethod(method, F)!.Invoke(win, null);
    private static Visibility Vis(MainWindow win, string name) => ((UIElement)win.FindName(name)).Visibility;

    [Fact]
    public Task HidesTheChromeAndRestoresTheWindow() => Ui.Run(async () =>
    {
        var win = new MainWindow();
        await win.OpenAsync(TempCopyOfSample());
        Assert.Equal(WindowStyle.SingleBorderWindow, win.WindowStyle);
        Assert.Equal(WindowState.Normal, win.WindowState);

        Call(win, "ToggleFullScreen");
        Assert.True(win.IsFullScreen);
        Assert.Equal(WindowStyle.None, win.WindowStyle);
        Assert.Equal(ResizeMode.NoResize, win.ResizeMode);
        Assert.Equal(WindowState.Maximized, win.WindowState);
        Assert.Equal(Visibility.Collapsed, Vis(win, "MainMenu"));
        Assert.Equal(Visibility.Collapsed, Vis(win, "ToolBars"));
        Assert.Equal(Visibility.Collapsed, Vis(win, "StatusBarPanel"));
        Assert.Equal(Visibility.Visible, Vis(win, "FullScreenBar"));
        // (The page number needs real layout; showing a full screen window would cover the desktop, so it isn't checked here.)
        Assert.EndsWith("%", ((System.Windows.Controls.TextBlock)win.FindName("FullScreenZoomText")).Text);
        Assert.True(((System.Windows.Controls.MenuItem)win.FindName("FullScreenMenu")).IsChecked);

        Call(win, "ToggleFullScreen");
        Assert.False(win.IsFullScreen);
        Assert.Equal(WindowStyle.SingleBorderWindow, win.WindowStyle);
        Assert.Equal(ResizeMode.CanResize, win.ResizeMode);
        Assert.Equal(WindowState.Normal, win.WindowState);
        Assert.Equal(Visibility.Visible, Vis(win, "MainMenu"));
        Assert.Equal(Visibility.Visible, Vis(win, "ToolBars"));
        Assert.Equal(Visibility.Visible, Vis(win, "StatusBarPanel"));
        Assert.Equal(Visibility.Collapsed, Vis(win, "FullScreenBar"));
        Assert.False(((System.Windows.Controls.MenuItem)win.FindName("FullScreenMenu")).IsChecked);
        typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false);
    });

    [Fact]
    public Task AMaximizedWindowIsMaximizedAgainAfterwards() => Ui.Run(() =>
    {
        var win = new MainWindow { WindowState = WindowState.Maximized };
        Call(win, "EnterFullScreen");
        Assert.Equal(WindowStyle.None, win.WindowStyle);
        Call(win, "ExitFullScreen");
        Assert.Equal(WindowState.Maximized, win.WindowState);
        Assert.Equal(WindowStyle.SingleBorderWindow, win.WindowStyle);
    });

    [Fact]
    public Task WorksWithoutADocument() => Ui.Run(() =>
    {
        var win = new MainWindow();
        Call(win, "ToggleFullScreen");
        Assert.True(win.IsFullScreen);
        Assert.Equal("", ((System.Windows.Controls.TextBlock)win.FindName("FullScreenPageText")).Text);
        Call(win, "ToggleFullScreen");
        Assert.False(win.IsFullScreen);
    });
}
