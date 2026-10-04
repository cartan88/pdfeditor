using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Services;

namespace PdfEditor.Dialogs;

public partial class DrawSignatureDialog : Window
{
    private static readonly Color BlueInk = Color.FromRgb(0x1A, 0x3A, 0x9C);

    public DrawSignatureDialog()
    {
        InitializeComponent();
        ApplyPen();
    }

    public byte[]? Png { get; private set; }

    private void ApplyPen()
    {
        if (Ink == null) return;
        var da = Ink.DefaultDrawingAttributes;
        da.Color = InkBlue?.IsChecked == true ? BlueInk : Colors.Black;
        da.Width = da.Height = ThicknessSlider?.Value ?? 3;
        da.FitToCurve = true;
        da.IgnorePressure = false;
        foreach (var s in Ink.Strokes)
        {
            s.DrawingAttributes.Color = da.Color;
            s.DrawingAttributes.Width = s.DrawingAttributes.Height = da.Width;
        }
    }

    private void Ink_Changed(object sender, RoutedEventArgs e) => ApplyPen();
    private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyPen();
    private void Clear_Click(object sender, RoutedEventArgs e) => Ink.Strokes.Clear();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count == 0)
        {
            MessageBox.Show(this, "Please draw your signature first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Render the strokes at 4x for a crisp result, cropped to their bounds.
        const double scale = 4;
        var bounds = Ink.Strokes.GetBounds();
        bounds.Inflate(4, 4);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            Ink.Strokes.Draw(dc);
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        Png = ImageUtil.ToPng(ImageUtil.PrepareSignature(rtb, removeBackground: false));
        DialogResult = true;
    }
}
