using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Controls;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>
/// Field text must land in the same place on screen (the WPF controls) and in the saved file (the appearance streams
/// rendered by the Windows PDF engine). Both are rendered at 4 px per point and the ink of each field is compared.
/// </summary>
public class FieldTextParityTests
{
    private const double Scale = 4;

    private static readonly (FieldSpec Spec, string Value)[] Cases =
    {
        (new("auto_short", 100, 720, 200, 20), "Short value"),
        (new("auto_long", 100, 686, 150, 20), "A much longer value that cannot fit"),
        (new("auto_tall", 100, 632, 200, 40), "Tall box"),
        (new("auto_small", 100, 606, 120, 12), "Small box"),
        (new("fixed9", 100, 574, 200, 18, "/Helv 9 Tf 0 g"), "Fixed nine point"),
        (new("centred", 100, 540, 200, 20, "/Helv 10 Tf 0 g", Q: 1), "Centred text"),
        (new("right", 100, 506, 200, 20, "/Helv 10 Tf 0 g", Q: 2), "Right aligned"),
        (new("red", 100, 472, 200, 20, "/Helv 11 Tf 1 0 0 rg"), "Red text"),
        (new("courier", 100, 438, 200, 20, "/Cour 10 Tf 0 g"), "Courier text"),
        (new("times", 100, 404, 200, 20, "/TiRo 11 Tf 0 g"), "Times text"),
        (new("multiline", 100, 326, 200, 64, "/Helv 10 Tf 0 g", Ff: 1 << 12), "Line one is long enough to wrap around the edge of the box\nSecond paragraph"),
        (new("comb", 100, 292, 120, 20, "/Helv 12 Tf 0 g", Ff: 1 << 24, MaxLen: 6), "123456"),
        (new("password", 100, 258, 200, 20, "/Helv 10 Tf 0 g", Ff: 1 << 13), "secret"),
    };

    [Fact]
    public Task ScreenAndSavedTextMatchWithinHalfAPoint() => Ui.Run(async () =>
    {
        var form = Form(Cases.Select(c => c.Spec));
        var pdf = PdfOpen.Open(form, null);
        var geometry = PdfOpen.ReadGeometry(pdf)[0];
        var models = FormReader.Read(pdf).Select(b => b.Model).ToList();
        foreach (var (spec, value) in Cases) models.First(m => m.FullName == spec.Name).Value = value;

        // Saved: flattened, so exactly the generated appearance streams are drawn.
        var saved = PdfSaver.Build(form, null, models, new List<StampModel>(), flatten: true);
        BitmapSource savedBmp;
        using (var r = await PdfRenderer.LoadAsync(saved, null)) savedBmp = await r.RenderAsync(0, (int)(612 * Scale));

        // Screen: the real controls, laid out in memory (no window), rendered at the same scale.
        bool highlight = FieldOverlay.Highlight;
        FieldOverlay.Highlight = false; // no tint, so only the text is ink
        var page = new PageView(geometry, new ScaleTransform(1, 1));
        FieldOverlay.Build(page, models.SelectMany(m => m.Widgets), (w, rr) => { });
        FieldOverlay.Highlight = highlight;
        page.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        page.Arrange(new Rect(page.DesiredSize));
        page.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 612 * Scale, 792 * Scale));
            dc.PushTransform(new ScaleTransform(Scale, Scale));
            dc.DrawRectangle(new VisualBrush(page.FieldLayer)
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
        foreach (var (spec, _) in Cases)
        {
            var rect = geometry.PdfRectToView(models.First(m => m.FullName == spec.Name).Widgets[0].PdfRect);
            var a = Ink(cp, screen.PixelWidth * 4, rect);
            var b = Ink(sp, savedBmp.PixelWidth * 4, rect);
            if (a == null || b == null) { problems.Add($"{spec.Name}: no text found ({(a == null ? "screen" : "saved")})"); continue; }
            double worst = new[] { a.Value.X - b.Value.X, a.Value.Y - b.Value.Y, a.Value.Width - b.Value.Width, a.Value.Height - b.Value.Height }.Max(Math.Abs);
            if (worst > 0.5) problems.Add($"{spec.Name}: screen {a} vs saved {b} (off by {worst:F2} pt)");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    });

    /// <summary>Bounding box of dark pixels inside a field, in points relative to the field.</summary>
    private static Rect? Ink(byte[] px, int stride, Rect field)
    {
        int x0 = (int)Math.Ceiling(field.X * Scale) + 2, y0 = (int)Math.Ceiling(field.Y * Scale) + 2;
        int x1 = (int)Math.Floor(field.Right * Scale) - 2, y1 = (int)Math.Floor(field.Bottom * Scale) - 2;
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
        return new Rect(minX / Scale - field.X, minY / Scale - field.Y, (maxX - minX + 1) / Scale, (maxY - minY + 1) / Scale);
    }
}
