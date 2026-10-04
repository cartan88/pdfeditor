using System.Windows;
using System.Windows.Media;

namespace PdfEditor.Models;

/// <summary>
/// Maps between "view" space (what the user sees: points, origin top-left of the visible
/// crop box, page rotation applied) and PDF user space (points, origin bottom-left, unrotated).
/// </summary>
public sealed class PageGeometry
{
    public PageGeometry(int index, Rect cropBox, int rotation, double mediaHeight)
    {
        Index = index;
        CropBox = cropBox;
        Rotation = ((rotation % 360) + 360) % 360;
        MediaHeight = mediaHeight;

        double l = cropBox.Left, b = cropBox.Top, r = cropBox.Right, t = cropBox.Bottom; // Rect.Top is the min y
        // Row-vector convention: pdf = view * M  (px = vx*M11 + vy*M21 + OffsetX, py = vx*M12 + vy*M22 + OffsetY)
        ViewToPdf = Rotation switch
        {
            90 => new Matrix(0, 1, 1, 0, l, b),
            180 => new Matrix(-1, 0, 0, 1, r, b),
            270 => new Matrix(0, -1, -1, 0, r, t),
            _ => new Matrix(1, 0, 0, -1, l, t),
        };
        var inv = ViewToPdf;
        inv.Invert();
        PdfToView = inv;
    }

    public int Index { get; }

    /// <summary>Visible area in PDF user space (Rect.Y is the bottom edge).</summary>
    public Rect CropBox { get; }

    public int Rotation { get; }

    /// <summary>Height of the media box; PDFsharp's XGraphics flips y around this value.</summary>
    public double MediaHeight { get; }

    public double ViewWidth => Rotation % 180 == 0 ? CropBox.Width : CropBox.Height;
    public double ViewHeight => Rotation % 180 == 0 ? CropBox.Height : CropBox.Width;

    public Matrix ViewToPdf { get; }
    public Matrix PdfToView { get; }

    public Rect PdfRectToView(Rect pdfRect)
    {
        var a = PdfToView.Transform(pdfRect.TopLeft);
        var c = PdfToView.Transform(pdfRect.BottomRight);
        return new Rect(a, c);
    }

    /// <summary>Matrix that maps view space to PDFsharp XGraphics page space.</summary>
    public Matrix ViewToXGraphics
    {
        get
        {
            var m = ViewToPdf;
            m.Append(new Matrix(1, 0, 0, -1, 0, MediaHeight));
            return m;
        }
    }
}
