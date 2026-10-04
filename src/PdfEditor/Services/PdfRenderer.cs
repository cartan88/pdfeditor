using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace PdfEditor.Services;

/// <summary>Renders pages to bitmaps using the PDF engine built into Windows.</summary>
public sealed class PdfRenderer : IDisposable
{
    private readonly WinPdfDocument _doc;
    private readonly InMemoryRandomAccessStream _source;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PdfRenderer(WinPdfDocument doc, InMemoryRandomAccessStream source)
    {
        _doc = doc;
        _source = source;
    }

    public int PageCount => (int)_doc.PageCount;

    public static async Task<PdfRenderer> LoadAsync(byte[] bytes, string? password)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var doc = string.IsNullOrEmpty(password)
            ? await WinPdfDocument.LoadFromStreamAsync(ras)
            : await WinPdfDocument.LoadFromStreamAsync(ras, password);
        return new PdfRenderer(doc, ras);
    }

    public async Task<BitmapSource> RenderAsync(int pageIndex, int pixelWidth)
    {
        await _gate.WaitAsync();
        try
        {
            using var page = _doc.GetPage((uint)pageIndex);
            using var output = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Clamp(pixelWidth, 16, 8000) };
            await page.RenderToStreamAsync(output, options);

            var ms = new MemoryStream();
            using (var s = output.AsStreamForRead()) await s.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _source.Dispose();
        _gate.Dispose();
    }
}
