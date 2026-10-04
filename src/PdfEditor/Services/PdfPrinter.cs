using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfEditor.Models;

namespace PdfEditor.Services;

/// <summary>
/// Prints a finished (flattened) PDF by rendering each page with the Windows PDF engine and sending it
/// to the printer one page at a time, so memory use stays flat for long documents.
/// </summary>
public static class PdfPrinter
{
    private const double PointsToDip = 96.0 / 72.0;

    public static async Task PrintAsync(byte[] pdf, string? password, IReadOnlyList<PageGeometry> geometry,
        IReadOnlyList<int> pages, PrintDialog dialog, string jobName, Action<int, int>? progress = null)
    {
        using var renderer = await LoadRendererAsync(pdf, password);
        var queue = dialog.PrintQueue;
        var ticket = dialog.PrintTicket;
        var area = ImageableArea(dialog);
        double dpi = Math.Clamp(ticket.PageResolution?.X ?? 300, 200, 300);

        queue.CurrentJobSettings.Description = jobName;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var collator = writer.CreateVisualsCollator(ticket, ticket);
        collator.BeginBatchWrite();
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                progress?.Invoke(i + 1, pages.Count);
                collator.Write(await ComposePageAsync(renderer, geometry[pages[i]], area, dpi));
            }
            collator.EndBatchWrite();
        }
        catch
        {
            try { collator.Cancel(); } catch { /* already failed */ }
            throw;
        }
    }

    /// <summary>
    /// Builds one printed sheet: the page is centred in the printable area, shrunk to fit if it is too large,
    /// and turned 90° when its orientation differs from the paper's.
    /// </summary>
    public static async Task<DrawingVisual> ComposePageAsync(PdfRenderer renderer, PageGeometry page, Rect area, double dpi)
    {
        double pw = page.ViewWidth * PointsToDip, ph = page.ViewHeight * PointsToDip;
        bool rotate = Math.Abs(pw - ph) > 1 && (pw > ph) != (area.Width > area.Height);
        double footprintW = rotate ? ph : pw, footprintH = rotate ? pw : ph;
        double scale = Math.Min(1, Math.Min(area.Width / footprintW, area.Height / footprintH));
        double w = pw * scale, h = ph * scale;

        var bmp = await renderer.RenderAsync(page.Index, (int)Math.Ceiling(w / 96 * dpi));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(area.X + area.Width / 2, area.Y + area.Height / 2));
            // Counter-clockwise puts a landscape page's top along the sheet's left edge (the usual convention) and
            // undoes a /Rotate 90 page; a /Rotate 270 page needs the opposite turn to come out upright.
            if (rotate) dc.PushTransform(new RotateTransform(page.Rotation == 270 ? 90 : -90));
            dc.DrawImage(bmp, new Rect(-w / 2, -h / 2, w, h));
        }
        return visual;
    }

    /// <summary>The part of the sheet the printer can actually print on, in DIPs from the sheet's top-left.</summary>
    private static Rect ImageableArea(PrintDialog dialog)
    {
        var sheet = new Rect(0, 0, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        try
        {
            if (dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket).PageImageableArea is { ExtentWidth: > 0, ExtentHeight: > 0 } ia)
            {
                var r = Rect.Intersect(sheet, new Rect(ia.OriginWidth, ia.OriginHeight, ia.ExtentWidth, ia.ExtentHeight));
                if (!r.IsEmpty && r.Width > 50 && r.Height > 50) return r;
            }
        }
        catch (Exception) { /* some drivers don't report capabilities; use the whole sheet */ }
        return sheet;
    }

    /// <summary>The saved copy may or may not keep the original encryption, so try without the password first.</summary>
    private static async Task<PdfRenderer> LoadRendererAsync(byte[] pdf, string? password)
    {
        try
        {
            return await PdfRenderer.LoadAsync(pdf, null);
        }
        catch (Exception) when (!string.IsNullOrEmpty(password))
        {
            return await PdfRenderer.LoadAsync(pdf, password);
        }
    }
}
