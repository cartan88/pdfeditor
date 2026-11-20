using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfEditor.Controls;
using PdfEditor.Models;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>
/// Arrow keys, Page Up/Down and Home/End scroll the document. Scrolling needs real layout, so the window is shown far
/// off-screen (never activated, not in the taskbar) and closed afterwards with nothing to save.
/// </summary>
public class KeyboardScrollTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

    private static async Task WithDocument(Func<MainWindow, ScrollViewer, Task> body)
    {
        var win = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1100, Height = 850,
            ShowActivated = false, ShowInTaskbar = false,
        };
        win.Show();
        try
        {
            await win.OpenAsync(TempCopyOfSample()); // two pages
            win.UpdateLayout();
            await body(win, (ScrollViewer)win.FindName("Scroller"));
        }
        finally
        {
            typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false);
            win.Close();
        }
    }

    /// <summary>Sends a key press through the window's keyboard handling, as typing would.</summary>
    private static bool Press(MainWindow win, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(win)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        win.RaiseEvent(args);
        win.UpdateLayout();
        return args.Handled;
    }

    [Fact]
    public Task UpDownPageAndHomeEndScroll() => Ui.Run(() => WithDocument(async (win, scroller) =>
    {
        Assert.Equal(0, scroller.VerticalOffset);
        Assert.True(Press(win, Key.Down));
        Assert.Equal(48, scroller.VerticalOffset, 1);
        Press(win, Key.Down);
        Assert.Equal(96, scroller.VerticalOffset, 1);
        Assert.True(Press(win, Key.Up));
        Assert.Equal(48, scroller.VerticalOffset, 1);

        Assert.True(Press(win, Key.PageDown));
        Assert.Equal(48 + scroller.ViewportHeight * 0.9, scroller.VerticalOffset, 1);
        Assert.True(Press(win, Key.End));
        Assert.Equal(scroller.ScrollableHeight, scroller.VerticalOffset, 1);
        Assert.True(Press(win, Key.Home));
        Assert.Equal(0, scroller.VerticalOffset);
        await Task.CompletedTask;
    }));

    [Fact]
    public Task LeftAndRightChangePageWhenThePageFitsTheWidth() => Ui.Run(() => WithDocument(async (win, scroller) =>
    {
        Assert.True(scroller.ScrollableWidth <= 1); // fit width: nothing to scroll sideways
        var pages = (List<PageView>)typeof(MainWindow).GetField("_pages", F)!.GetValue(win)!;
        Assert.True(Press(win, Key.Right));
        double page2 = pages[1].TransformToAncestor((System.Windows.Media.Visual)scroller.Content).Transform(new Point()).Y;
        // Page 2 at the top: its position in the page list, plus the list's 24 px margin, less GoToPage's 8 px gap.
        Assert.Equal(page2 + 24 - 8, scroller.VerticalOffset, 1);
        Assert.True(Press(win, Key.Left));
        Assert.True(scroller.VerticalOffset < 40);
        await Task.CompletedTask;
    }));

    [Fact]
    public Task ArrowsStillNudgeASelectedItem() => Ui.Run(() => WithDocument(async (win, scroller) =>
    {
        var item = new StampModel { Kind = StampKind.Whiteout, PageIndex = 0 };
        item.Width = 50; item.Height = 20; item.CenterX = 100; item.CenterY = 100;
        typeof(MainWindow).GetMethod("AddStamp", F)!.Invoke(win, new object[] { item, true }); // added and selected
        Assert.True(Press(win, Key.Down));
        Assert.Equal(101, item.CenterY);
        Assert.Equal(0, scroller.VerticalOffset); // the page didn't scroll
        await Task.CompletedTask;
    }));
}
