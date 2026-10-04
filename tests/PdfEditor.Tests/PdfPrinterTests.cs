using System.IO;
using System.Windows;
using System.Windows.Xps.Packaging;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

public class PdfPrinterTests
{
    private static readonly Rect Area = new(24, 24, 768, 1008); // US Letter in DIPs with 0.25" margins

    /// <summary>Portrait, landscape, oversized A2, /Rotate 90, and a small page.</summary>
    private static byte[] MixedPages()
    {
        var doc = new PdfDocument();
        void Add(double w, double h, int rotate = 0)
        {
            var p = doc.AddPage(); p.Width = XUnit.FromPoint(w); p.Height = XUnit.FromPoint(h);
            using (var g = XGraphics.FromPdfPage(p))
            {
                g.DrawRectangle(new XPen(XColors.Black, 6), 3, 3, w - 6, h - 6);
                g.DrawRectangle(XBrushes.Black, w / 2 - 40, 20, 80, 30); // "top" marker
            }
            if (rotate != 0) p.Elements["/Rotate"] = new PdfInteger(rotate);
        }
        Add(612, 792); Add(792, 612); Add(1191, 1684); Add(612, 792, 90); Add(400, 300);
        return Save(doc);
    }

    [Theory]
    [InlineData(0, false, false)] // portrait letter: shrunk slightly to fit the margins
    [InlineData(1, true, false)]  // landscape: turned to fit portrait paper
    [InlineData(2, false, false)] // A2: shrunk to fit
    [InlineData(3, true, false)]  // shown landscape because of /Rotate 90: turned back
    [InlineData(4, true, true)]   // small landscape page: turned, printed at actual size
    public Task PagesAreFittedToThePaper(int index, bool turned, bool actualSize) => Ui.Run(async () =>
    {
        var pdf = MixedPages();
        var geometry = PdfOpen.ReadGeometry(PdfOpen.Open(pdf, null))[index];
        using var r = await PdfRenderer.LoadAsync(pdf, null);
        var bounds = (await PdfPrinter.ComposePageAsync(r, geometry, Area, 200)).ContentBounds;

        Assert.True(bounds.Left >= Area.Left - 0.5 && bounds.Top >= Area.Top - 0.5 && bounds.Right <= Area.Right + 0.5 && bounds.Bottom <= Area.Bottom + 0.5,
            $"drawn at {bounds}, outside the printable area");
        double pw = geometry.ViewWidth * 96 / 72, ph = geometry.ViewHeight * 96 / 72;
        Assert.Equal(turned, (bounds.Width > bounds.Height) != (pw > ph));
        if (actualSize) Assert.Equal(Math.Max(pw, ph), Math.Max(bounds.Width, bounds.Height), 0);
        else Assert.True(Math.Max(bounds.Width, bounds.Height) <= Math.Max(pw, ph) + 0.5);
    });

    [Fact]
    public Task RotatedPagePrintsUpright() => Ui.Run(async () =>
    {
        // Page 4 is portrait content shown landscape by /Rotate 90; printed on portrait paper it must come out upright,
        // with the top marker at the top of the sheet.
        var pdf = MixedPages();
        var geometry = PdfOpen.ReadGeometry(PdfOpen.Open(pdf, null))[3];
        using var r = await PdfRenderer.LoadAsync(pdf, null);
        var visual = await PdfPrinter.ComposePageAsync(r, geometry, Area, 100);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(816, 1056, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        var b = visual.ContentBounds;
        var top = PixelAt(rtb, (int)(b.Left + b.Width / 2), (int)(b.Top + 30 * b.Height / 792 + 8));
        var bottom = PixelAt(rtb, (int)(b.Left + b.Width / 2), (int)(b.Bottom - 30 * b.Height / 792 - 8));
        Assert.True(top.R < 80, $"top marker missing ({top})");
        Assert.True(bottom.R > 200, $"marker printed at the bottom ({bottom})");
    });

    [Fact]
    public Task PrintJobContainsEveryPageOfTheFlattenedDocument() => Ui.Run(async () =>
    {
        // Same path as Print: flattened build, one composed sheet per page, batched into an XPS document.
        var original = SampleForm();
        var fields = Fields(original);
        fields.First(f => f.FullName == "full_name").Value = "PRINTED";
        var printable = PdfSaver.Build(original, null, fields, new List<StampModel>(), flatten: true);
        var geometry = PdfOpen.ReadGeometry(PdfOpen.Open(printable, null));
        using var r = await PdfRenderer.LoadAsync(printable, null);
        var path = Path.Combine(TempDir(), "job.xps");
        using (var xps = new XpsDocument(path, FileAccess.ReadWrite))
        {
            var collator = XpsDocument.CreateXpsDocumentWriter(xps).CreateVisualsCollator();
            collator.BeginBatchWrite();
            foreach (var g in geometry) collator.Write(await PdfPrinter.ComposePageAsync(r, g, Area, 150));
            collator.EndBatchWrite();
        }
        using var read = new XpsDocument(path, FileAccess.Read);
        Assert.Equal(geometry.Count, read.GetFixedDocumentSequence().DocumentPaginator.PageCount);
    });
}
