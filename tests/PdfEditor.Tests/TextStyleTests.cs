using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Controls;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Pdf;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>Font, bold and italic for placed text: on screen, in the saved file, after reopening, and in the toolbar.</summary>
public class TextStyleTests
{
    private const double Scale = 4;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly string[] Fonts = { "Arial", "Calibri", "Georgia", "Times New Roman", "Courier New", "Segoe UI", "Verdana", "Segoe Script" };

    private static IEnumerable<(string Font, bool Bold, bool Italic)> Styles() =>
        Fonts.Where(Installed).SelectMany(f => new[] { (f, false, false), (f, true, false), (f, false, true), (f, true, true) });

    private static bool Installed(string font) => System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source == font);

    private static List<StampModel> StyledStamps()
    {
        var list = new List<StampModel>();
        double y = 40;
        foreach (var (font, bold, italic) in Styles())
        {
            var m = Item(StampKind.Text, 0, 0, 0, 1, 1, text: $"{font} {(bold ? "Bold " : "")}{(italic ? "Italic" : "")} Tqy", size: 11, font: font);
            m.Bold = bold; m.Italic = italic;
            var size = StampView.MeasureText(m);
            m.Width = size.Width; m.Height = size.Height;
            m.CenterX = 60 + m.Width / 2; m.CenterY = y;
            y += 22;
            if (y > 770) break;
            list.Add(m);
        }
        return list;
    }

    [Fact]
    public void BoldAndItalicFontsAreEmbeddedInTheSavedFile()
    {
        var stamps = StyledStamps();
        var saved = PdfOpen.Open(PdfSaver.Build(TextPages(1), null, new List<FormFieldModel>(), stamps, flatten: false), null);
        var fonts = saved.Pages[0].Resources.Elements.GetDictionary("/Font")!.Elements.Values
            .Select(v => ((PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)v).Value).Elements.GetName("/BaseFont"))
            .ToList();
        // One embedded font per font/style combination (PDFsharp may simulate a style a font doesn't have).
        Assert.True(fonts.Count >= stamps.Select(s => (s.FontFamily, s.Bold, s.Italic)).Distinct().Count() / 2,
            "fonts embedded: " + string.Join(", ", fonts));
        Assert.Contains(fonts, f => f.Contains("Arial", StringComparison.OrdinalIgnoreCase) && f.Contains("Bold", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fonts, f => f.Contains("Georgia", StringComparison.OrdinalIgnoreCase) && f.Contains("Italic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FontAndStyleSurviveSavingAndReopening()
    {
        var stamps = StyledStamps();
        var result = StampStore.Extract(PdfSaver.Build(TextPages(1), null, new List<FormFieldModel>(), stamps, flatten: false), null);
        Assert.Equal(stamps.Count, result.Stamps.Count);
        Assert.Equal(stamps.Select(s => (s.FontFamily, s.Bold, s.Italic)), result.Stamps.Select(s => (s.FontFamily, s.Bold, s.Italic)));
    }

    [Fact]
    public Task ScreenAndSavedTextMatchForEveryFontAndStyle() => Ui.Run(async () =>
    {
        var blank = TextPages(1, (n, p) => p.Contents.Elements.Clear()); // empty page: only the stamps have ink
        var stamps = StyledStamps();
        var geometry = PdfOpen.ReadGeometry(PdfOpen.Open(blank, null))[0];

        var saved = PdfSaver.Build(blank, null, new List<FormFieldModel>(), stamps, flatten: true);
        BitmapSource savedBmp;
        using (var r = await PdfRenderer.LoadAsync(saved, null)) savedBmp = await r.RenderAsync(0, (int)(612 * Scale));

        var page = new PageView(geometry, new ScaleTransform(1, 1));
        foreach (var s in stamps) page.StampLayer.Children.Add(new StampView(s));
        page.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        page.Arrange(new Rect(page.DesiredSize));
        page.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 612 * Scale, 792 * Scale));
            dc.PushTransform(new ScaleTransform(Scale, Scale));
            dc.DrawRectangle(new VisualBrush(page.StampLayer)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 612, 792),
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 612, 792),
            }, null, new Rect(0, 0, 612, 792));
        }
        var screen = new RenderTargetBitmap((int)(612 * Scale), (int)(792 * Scale), 96, 96, PixelFormats.Pbgra32);
        screen.Render(dv);

        byte[] sp = Pixels(savedBmp), cp = Pixels(screen);
        var problems = new List<string>();
        foreach (var s in stamps)
        {
            var box = new Rect(s.CenterX - s.Width / 2, s.CenterY - s.Height / 2, s.Width + 30, s.Height); // a little slack on the right
            var a = Ink(cp, screen.PixelWidth * 4, box);
            var b = Ink(sp, savedBmp.PixelWidth * 4, box);
            if (a == null || b == null) { problems.Add($"{s.Text}: no text ({(a == null ? "screen" : "saved")})"); continue; }
            double worst = new[] { a.Value.X - b.Value.X, a.Value.Y - b.Value.Y, a.Value.Width - b.Value.Width, a.Value.Height - b.Value.Height }.Max(Math.Abs);
            // A style the font doesn't have (e.g. Segoe Script has no italic) is faked by slanting / emboldening,
            // which WPF and PDFsharp do very slightly differently; allow a little more for those.
            bool simulated = StampView.TypefaceFor(s).TryGetGlyphTypeface(out var gt) && gt.StyleSimulations != StyleSimulations.None;
            double limit = simulated ? 1.25 : 0.75;
            if (worst > limit) problems.Add($"{s.Text}: screen {a} vs saved {b} (off by {worst:F2} pt, limit {limit})");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    });

    private static Rect? Ink(byte[] px, int stride, Rect box)
    {
        int x0 = Math.Max(0, (int)(box.X * Scale)), y0 = Math.Max(0, (int)(box.Y * Scale));
        int x1 = Math.Min(stride / 4, (int)(box.Right * Scale)), y1 = Math.Min(px.Length / stride, (int)(box.Bottom * Scale));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * stride + x * 4;
                if (Math.Min(px[i + 2], Math.Min(px[i + 1], px[i])) < 150)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        if (maxX < 0) return null;
        return new Rect(minX / Scale - box.X, minY / Scale - box.Y, (maxX - minX + 1) / Scale, (maxY - minY + 1) / Scale);
    }

    // ------------------------------------------------------------------ toolbar

    private static async Task<(MainWindow Win, Func<string, object?> Get)> OpenWindow(bool resetPreferences = true)
    {
        if (resetPreferences) new UserSettings().Save(); // each test starts from the default text style
        var win = new MainWindow();
        await win.OpenAsync(TempCopyOfSample());
        return (win, name => typeof(MainWindow).GetField(name, F)!.GetValue(win));
    }

    private static StampModel PlaceText(MainWindow win, string text)
    {
        var m = Item(StampKind.Text, 0, 300, 100, 80, 16, text: text);
        typeof(MainWindow).GetMethod("Mutate", F)!.Invoke(win, new object[] { (Action)(() =>
            typeof(MainWindow).GetMethod("AddStamp", F)!.Invoke(win, new object[] { m, true })) });
        return m;
    }

    [Fact]
    public Task FontListShowsCommonFontsFirstThenEveryInstalledFont() => Ui.Run(async () =>
    {
        var (win, get) = await OpenWindow();
        try
        {
            var box = (ComboBox)win.FindName("FontBox");
            var items = box.Items.Cast<object>().ToList();
            int sep = items.FindIndex(i => i is Separator);
            Assert.True(sep > 0, "separator after the common fonts");
            Assert.Equal("Arial", (string)((ComboBoxItem)items[0]).Tag);
            var all = items.Skip(sep + 1).Cast<ComboBoxItem>().Select(i => (string)i.Tag).ToList();
            Assert.Equal(all.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), all);
            Assert.True(all.Count >= System.Windows.Media.Fonts.SystemFontFamilies.Count / 2);
            var georgia = items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "Georgia");
            Assert.Equal("Georgia", ((TextBlock)georgia.Content).FontFamily.Source); // shown in its own typeface
        }
        finally { typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false); }
    });

    [Fact]
    public Task ToolbarChangesTheSelectedTextAndUndoRestoresIt() => Ui.Run(async () =>
    {
        var (win, get) = await OpenWindow();
        try
        {
            var m = PlaceText(win, "Styled");
            double width = m.Width;
            var box = (ComboBox)win.FindName("FontBox");
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "Georgia");
            Assert.Equal("Georgia", m.FontFamily);

            var bold = (ToggleButton)win.FindName("BoldButton");
            bold.IsChecked = true;
            bold.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(m.Bold);
            Assert.NotEqual(width, m.Width); // the box resized for the new font and weight

            // Ctrl+I calls SetItalic (Keyboard.Modifiers can't be faked in a test, so call it directly).
            typeof(MainWindow).GetMethod("SetItalic", F)!.Invoke(win, new object[] { true });
            Assert.True(m.Italic);
            Assert.True(((ToggleButton)win.FindName("ItalicButton")).IsChecked);

            var undo = typeof(MainWindow).GetMethod("Undo", F)!;
            undo.Invoke(win, null); // italic
            undo.Invoke(win, null); // bold
            undo.Invoke(win, null); // font
            var restored = ((System.Collections.IList)get("_stamps")!).Cast<object>().Select(v => (StampModel)v.GetType().GetProperty("Model")!.GetValue(v)!).Single();
            Assert.Equal(("Arial", false, false), (restored.FontFamily, restored.Bold, restored.Italic));
        }
        finally { typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false); }
    });

    [Fact]
    public Task TheLastFontAndStyleAreRememberedForNewText() => Ui.Run(async () =>
    {
        var (win, _) = await OpenWindow();
        try
        {
            typeof(MainWindow).GetMethod("Select", F)!.Invoke(win, new object?[] { null });
            var box = (ComboBox)win.FindName("FontBox");
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "Courier New");
            typeof(MainWindow).GetMethod("SetBold", F)!.Invoke(win, new object[] { true });
        }
        finally { typeof(MainWindow).GetField("_dirty", F)!.SetValue(win, false); }

        Assert.True(File.Exists(UserSettings.FilePath));
        Assert.NotEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfEditor", "settings.json"), UserSettings.FilePath);
        var settings = UserSettings.Load();
        Assert.Equal(("Courier New", true, false), (settings.TextFont, settings.TextBold, settings.TextItalic));

        // A new window starts with those, and new text uses them.
        var (win2, _) = await OpenWindow(resetPreferences: false);
        try
        {
            var m = (StampModel)typeof(MainWindow).GetMethod("NewText", F)!.Invoke(win2, new object[] { 0, "New", 12.0, Colors.Black })!;
            Assert.Equal(("Courier New", true, false), (m.FontFamily, m.Bold, m.Italic));
        }
        finally
        {
            typeof(MainWindow).GetField("_dirty", F)!.SetValue(win2, false);
            new UserSettings().Save(); // back to defaults for other tests
        }
    });
}
