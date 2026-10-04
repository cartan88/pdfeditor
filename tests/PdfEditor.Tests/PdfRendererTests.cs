using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Pdf;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

public class PdfRendererTests
{
    /// <summary>Page 1 plain, page 3 /Rotate 90, page 4 with a crop box.</summary>
    private static byte[] Pages() => TextPages(4, (n, p) =>
    {
        if (n == 2) p.Elements["/Rotate"] = new PdfInteger(90);
        if (n == 3) p.Elements["/CropBox"] = new PdfArray(p.Owner, new PdfReal(30), new PdfReal(100), new PdfReal(500), new PdfReal(700));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public Task RegionRenderLinesUpWithFullRender(int page) => Ui.Run(async () =>
    {
        // The zoomed-in detail image is a region render laid over the page; it must match the same area of a full render.
        using var r = await PdfRenderer.LoadAsync(Pages(), null);
        var full = await r.RenderAsync(page, 1600);
        var frac = new Rect(0.30, 0.20, 0.40, 0.35);
        int x0 = (int)Math.Round(frac.X * full.PixelWidth), y0 = (int)Math.Round(frac.Y * full.PixelHeight);
        var part = await r.RenderAsync(page, (int)Math.Round(frac.Width * full.PixelWidth), frac);
        int h = Math.Min(part.PixelHeight, full.PixelHeight - y0);
        var a = Pixels(new CroppedBitmap(part, new Int32Rect(0, 0, part.PixelWidth, h)));
        var b = Pixels(new CroppedBitmap(full, new Int32Rect(x0, y0, part.PixelWidth, h)));
        Assert.True(MeanDiff(a, b) < 6, $"mean difference {MeanDiff(a, b):F2}/255");
    });

    [Fact]
    public Task DecodesTheSameAsWindowsImaging() => Ui.Run(async () =>
    {
        var bytes = Pages();
        using var r = await PdfRenderer.LoadAsync(bytes, null);
        var mine = await r.RenderAsync(0, 500);

        var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var w = new Windows.Storage.Streams.DataWriter(ras)) { w.WriteBytes(bytes); await w.StoreAsync(); w.DetachStream(); }
        ras.Seek(0);
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(ras);
        var raw = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var p = doc.GetPage(0))
            await p.RenderToStreamAsync(raw, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 500, BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId });
        var encoded = new byte[raw.Size];
        using (var s = raw.AsStreamForRead()) s.ReadExactly(encoded);
        var wic = BitmapFrame.Create(new MemoryStream(encoded), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        Assert.Equal(Pixels(wic), Pixels(mine));
    });

    [Fact]
    public Task DisposingDuringARenderIsSafe() => Ui.Run(async () =>
    {
        var r = await PdfRenderer.LoadAsync(Pages(), null);
        var inflight = r.RenderAsync(0, 1200);
        r.Dispose();
        var bmp = await inflight; // the render that was already running completes
        Assert.True(bmp.PixelWidth > 0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => r.RenderAsync(0, 100));
    });
}
