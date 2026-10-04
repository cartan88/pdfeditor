using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEditor.Services;

namespace PdfEditor.Models;

public enum StampKind { Text, Image, Whiteout }

/// <summary>
/// Something the user placed on a page (text, signature/image, white-out box).
/// Geometry is in view space points: centre, unrotated size, and clockwise rotation in degrees.
/// </summary>
public sealed class StampModel : INotifyPropertyChanged
{
    private double _centerX, _centerY, _width, _height, _angle, _fontSize = 12;
    private string _text = "";
    private Color _color = Colors.Black;
    private string _fontFamily = "Arial";

    public required StampKind Kind { get; init; }
    public required int PageIndex { get; init; }

    /// <summary>Encoded image bytes (PNG or JPEG) for image stamps.</summary>
    public byte[]? ImageData { get; init; }

    private BitmapSource? _image;
    public BitmapSource? Image => _image ??= ImageData != null ? ImageUtil.FromBytes(ImageData) : null;

    public double CenterX { get => _centerX; set => Set(ref _centerX, value); }
    public double CenterY { get => _centerY; set => Set(ref _centerY, value); }
    public double Width { get => _width; set => Set(ref _width, Math.Max(2, value)); }
    public double Height { get => _height; set => Set(ref _height, Math.Max(2, value)); }

    /// <summary>Clockwise rotation in degrees, normalised to [0, 360).</summary>
    public double Angle
    {
        get => _angle;
        set => Set(ref _angle, ((value % 360) + 360) % 360);
    }

    public string Text { get => _text; set => Set(ref _text, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 4, 144)); }
    public Color Color { get => _color; set => Set(ref _color, value); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

    public double AspectRatio => Height > 0 ? Width / Height : 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
