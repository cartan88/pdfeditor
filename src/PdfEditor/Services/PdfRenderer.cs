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
    private bool _disposed;

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

    /// <summary>Renders a page, or part of it, to a bitmap <paramref name="pixelWidth"/> pixels wide.</summary>
    /// <param name="region">
    /// Part of the page to render, as fractions (0–1) of the page as displayed (rotation and crop box applied);
    /// null renders the whole page.
    /// </param>
    public async Task<BitmapSource> RenderAsync(int pageIndex, int pixelWidth, System.Windows.Rect? region = null)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.GetPage((uint)pageIndex);
            using var output = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(pixelWidth, 16, 8000),
                // BMP is far cheaper to encode and decode than the default PNG.
                BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId,
            };
            if (region is { } r)
            {
                var size = page.Size; // DIPs, as displayed
                options.SourceRect = new Windows.Foundation.Rect(r.X * size.Width, r.Y * size.Height, r.Width * size.Width, r.Height * size.Height);
            }
            await page.RenderToStreamAsync(output, options);

            output.Seek(0);
            using var s = output.AsStreamForRead();
            return DecodeBmp(s);
        }
        finally
        {
            if (_disposed) _source.Dispose(); // Dispose() was called while this render was running
            _gate.Release();
        }
    }

    /// <summary>
    /// Turns the renderer's 32-bit BMP output into a frozen bitmap. Rows are streamed straight into native memory
    /// (flipped to top-down on the way), so no page-sized managed array is ever allocated: the .NET heap would
    /// otherwise keep that memory committed after a collection. BitmapImage is avoided because it keeps its
    /// source stream (a second copy of the pixels) alive.
    /// </summary>
    private static BitmapSource DecodeBmp(Stream s)
    {
        var header = new byte[54];
        s.ReadExactly(header);
        int offset = BitConverter.ToInt32(header, 10);
        int width = BitConverter.ToInt32(header, 18), height = BitConverter.ToInt32(header, 22);
        int bpp = BitConverter.ToInt16(header, 28), compression = BitConverter.ToInt32(header, 30);
        int stride = width * 4, rows = Math.Abs(height);
        if (header[0] != 'B' || header[1] != 'M' || bpp != 32 || (compression != 0 && compression != 3) || offset < header.Length || width <= 0 || rows == 0)
        {
            // Unexpected layout: let WIC decode it, then copy out so the encoded bytes can be released.
            var rest = new MemoryStream();
            rest.Write(header);
            s.CopyTo(rest);
            rest.Position = 0;
            var copy = new WriteableBitmap(BitmapFrame.Create(rest, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
            copy.Freeze();
            return copy;
        }

        var row = new byte[stride];
        for (int skip = offset - header.Length; skip > 0; skip -= s.Read(row, 0, Math.Min(skip, stride))) { }
        long size = (long)stride * rows;
        var pixels = System.Runtime.InteropServices.Marshal.AllocHGlobal((nint)size);
        try
        {
            for (int i = 0; i < rows; i++)
            {
                s.ReadExactly(row);
                int target = height > 0 ? rows - 1 - i : i; // positive height = bottom-up rows
                System.Runtime.InteropServices.Marshal.Copy(row, 0, pixels + (nint)((long)target * stride), stride);
            }
            var result = BitmapSource.Create(width, rows, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, (int)size, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pixels);
        }
    }

    /// <summary>
    /// Releases the document stream. If a render is in progress, the stream is released when it finishes;
    /// the gate is never disposed so an in-flight render can always release it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_gate.Wait(0))
        {
            _source.Dispose();
            _gate.Release();
        }
    }
}
