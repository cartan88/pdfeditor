using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PdfEditor.Models;

namespace PdfEditor.Controls;

/// <summary>
/// One page in the document view. All layers share a coordinate system in PDF points (view space);
/// the shared zoom transform scales them to screen size.
/// </summary>
public sealed class PageView : Border
{
    public PageView(PageGeometry geometry, Transform zoom)
    {
        Geometry = geometry;
        Margin = new Thickness(0, 0, 0, 18);
        HorizontalAlignment = HorizontalAlignment.Center;
        Background = Brushes.White;
        // A plain border rather than a DropShadowEffect: effects force expensive re-rendering of the whole page.
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xC5));
        BorderThickness = new Thickness(1);

        Surface = new Grid
        {
            Width = geometry.ViewWidth,
            Height = geometry.ViewHeight,
            LayoutTransform = zoom,
            Background = Brushes.White,
            ClipToBounds = false,
        };
        PageImage = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(PageImage, BitmapScalingMode.HighQuality);
        FieldLayer = new Canvas();
        StampLayer = new Canvas();
        Surface.Children.Add(PageImage);
        Surface.Children.Add(FieldLayer);
        Surface.Children.Add(StampLayer);
        Child = Surface;
    }

    public PageGeometry Geometry { get; }
    public int Index => Geometry.Index;
    public Grid Surface { get; }
    public Image PageImage { get; }
    public Canvas FieldLayer { get; }
    public Canvas StampLayer { get; }

    /// <summary>Pixel width of the bitmap currently shown (0 = none).</summary>
    public int RenderedWidth { get; set; }
    public int RequestedWidth { get; set; }
}
