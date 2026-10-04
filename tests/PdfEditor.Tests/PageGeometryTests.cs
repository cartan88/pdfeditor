using System.Windows;
using PdfEditor.Models;

namespace PdfEditor.Tests;

/// <summary>Mapping between view space (what the user sees, y down) and PDF user space (y up), for every page rotation.</summary>
public class PageGeometryTests
{
    private static readonly Rect Letter = new(0, 0, 612, 792);

    [Fact]
    public void UnrotatedPageFlipsY()
    {
        var g = new PageGeometry(0, Letter, 0, 792);
        Assert.Equal(612, g.ViewWidth);
        Assert.Equal(792, g.ViewHeight);
        // A widget 100 pt from the left and 50 pt above the bottom edge.
        var view = g.PdfRectToView(new Rect(100, 50, 200, 20));
        Assert.Equal(new Rect(100, 792 - 70, 200, 20), view);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-90)]
    [InlineData(450)]
    public void RotatedPagesRoundTrip(int rotation)
    {
        var g = new PageGeometry(0, Letter, rotation, 792);
        foreach (var p in new[] { new Point(0, 0), new Point(100, 50), new Point(612, 792), new Point(300, 400) })
        {
            var back = g.ViewToPdf.Transform(g.PdfToView.Transform(p));
            Assert.Equal(p.X, back.X, 9);
            Assert.Equal(p.Y, back.Y, 9);
        }
        bool sideways = ((rotation % 360) + 360) % 180 == 90;
        Assert.Equal(sideways ? 792 : 612, g.ViewWidth);
        Assert.Equal(sideways ? 612 : 792, g.ViewHeight);
    }

    [Fact]
    public void Rotate90PutsPdfBottomLeftAtViewTopLeft()
    {
        // /Rotate 90 turns the page clockwise: the PDF's bottom-left corner ends up at the top-left of the view.
        var g = new PageGeometry(0, Letter, 90, 792);
        var corner = g.PdfToView.Transform(new Point(0, 0));
        Assert.Equal(0, corner.X, 9);
        Assert.Equal(0, corner.Y, 9);
        var topLeft = g.PdfToView.Transform(new Point(0, 792));
        Assert.Equal(792, topLeft.X, 9);
        Assert.Equal(0, topLeft.Y, 9);
    }

    [Fact]
    public void CropBoxMovesTheViewOrigin()
    {
        var crop = new Rect(30, 100, 470, 600); // left 30, bottom 100, right 500, top 700
        var g = new PageGeometry(0, crop, 0, 792);
        Assert.Equal(470, g.ViewWidth);
        Assert.Equal(600, g.ViewHeight);
        var origin = g.PdfToView.Transform(new Point(30, 700)); // crop box top-left
        Assert.Equal(0, origin.X, 9);
        Assert.Equal(0, origin.Y, 9);
    }

    [Fact]
    public void ViewToXGraphicsMatchesPdfSharpsFlippedSpace()
    {
        // PDFsharp's XGraphics flips y around the media box height; a view point must land where PDF space says.
        var g = new PageGeometry(0, new Rect(30, 100, 470, 600), 0, 792);
        var view = new Point(10, 20);
        var pdf = g.ViewToPdf.Transform(view);
        var xg = g.ViewToXGraphics.Transform(view);
        Assert.Equal(pdf.X, xg.X, 9);
        Assert.Equal(792 - pdf.Y, xg.Y, 9);
    }
}
