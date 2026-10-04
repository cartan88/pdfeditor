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
        DetailImage = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
        var detailLayer = new Canvas { IsHitTestVisible = false };
        detailLayer.Children.Add(DetailImage);
        FieldLayer = new Canvas();
        StampLayer = new Canvas();
        Surface.Children.Add(PageImage);
        Surface.Children.Add(detailLayer);
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

    /// <summary>
    /// Sharp rendering of just the visible part of the page, used when the full page at the current zoom
    /// would be too large to keep in memory. Positioned in view space (points) over <see cref="PageImage"/>.
    /// </summary>
    public Image DetailImage { get; }

    /// <summary>Area covered by <see cref="DetailImage"/>, in view space points (empty = none).</summary>
    public Rect DetailRegion { get; private set; } = Rect.Empty;

    /// <summary>Pixels per point of <see cref="DetailImage"/>.</summary>
    public double DetailDensity { get; private set; }

    public void SetDetail(System.Windows.Media.Imaging.BitmapSource bitmap, Rect region)
    {
        DetailImage.Source = bitmap;
        DetailImage.Width = region.Width;
        DetailImage.Height = region.Height;
        Canvas.SetLeft(DetailImage, region.X);
        Canvas.SetTop(DetailImage, region.Y);
        DetailImage.Visibility = Visibility.Visible;
        DetailRegion = region;
        DetailDensity = bitmap.PixelWidth / region.Width;
    }

    public void ClearDetail()
    {
        if (DetailImage.Source == null) return;
        DetailImage.Source = null;
        DetailImage.Visibility = Visibility.Collapsed;
        DetailRegion = Rect.Empty;
        DetailDensity = 0;
    }

    /// <summary>Drops both bitmaps (page far off screen).</summary>
    public void ClearRender()
    {
        PageImage.Source = null;
        RenderedWidth = 0;
        ClearDetail();
    }
}
