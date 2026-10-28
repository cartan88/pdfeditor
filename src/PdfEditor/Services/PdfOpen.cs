using System.IO;
using System.Windows;
using PdfEditor.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Services;

public static class PdfOpen
{
    /// <summary>
    /// Opens a PDF for editing. Files with "file streams" (an /F entry on a stream), which PDFsharp can't read, are
    /// opened with that entry renamed; see <see cref="FileStreams"/> for resolving them afterwards.
    /// </summary>
    public static PdfDocument Open(byte[] bytes, string? password)
    {
        try
        {
            return OpenRaw(bytes, password);
        }
        catch (NotImplementedException) when (FileStreams.Neutralize(bytes) is { } patched)
        {
            return OpenRaw(patched, password);
        }
    }

    private static PdfDocument OpenRaw(byte[] bytes, string? password)
    {
        var stream = new MemoryStream(bytes, writable: false);
        return password == null
            ? PdfReader.Open(stream, PdfDocumentOpenMode.Modify)
            : PdfReader.Open(stream, password, PdfDocumentOpenMode.Modify);
    }

    public static List<PageGeometry> ReadGeometry(PdfDocument doc)
    {
        var list = new List<PageGeometry>(doc.PageCount);
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            var mb = page.MediaBox;
            var media = new Rect(new Point(mb.X1, mb.Y1), new Point(mb.X2, mb.Y2));
            var crop = media;
            if (page.Elements.ContainsKey("/CropBox") && PdfObjects.GetRect(page, "/CropBox") is { } cb)
            {
                var c = Rect.Intersect(media, cb);
                if (!c.IsEmpty && c.Width > 1 && c.Height > 1) crop = c;
            }
            int rotation = page.Elements.GetInteger("/Rotate");
            list.Add(new PageGeometry(i, crop, rotation, page.Height.Point)
            {
                ColumnTabOrder = page.Elements.GetName("/Tabs") == "/C",
            });
        }
        return list;
    }
}
