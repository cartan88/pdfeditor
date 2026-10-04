using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfEditor.Models;
using PdfEditor.Services;

namespace PdfEditor.Controls;

/// <summary>
/// Interactive view of a <see cref="StampModel"/>: drag to move, corner handle to resize,
/// top handle to rotate (Shift snaps to 15°), double-click text to edit.
/// </summary>
public sealed class StampView : Grid
{
    private const double HandlePixels = 10;
    private const double RotateArmPixels = 26;

    private readonly RotateTransform _rotate = new();
    private readonly Canvas _chrome = new() { IsHitTestVisible = true };
    private readonly Rectangle _outline;
    private readonly Rectangle _resizeHandle;
    private readonly Ellipse _rotateHandle;
    private readonly Line _rotateArm;
    private readonly FrameworkElement _content;
    private readonly TextBlock? _textBlock;
    private TextBox? _editor;
    private double _uiScale = 1;
    private bool _selected;

    // Gesture state
    private enum Gesture { None, Move, Resize, Rotate }
    private Gesture _gesture;
    private Point _startPointer;
    private double _startCx, _startCy, _startW, _startH, _startFont;
    private Point _anchor;

    public StampView(StampModel model)
    {
        Model = model;
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Background = Brushes.Transparent; // hit-testable everywhere

        switch (model.Kind)
        {
            case StampKind.Image:
                _content = new Image { Source = model.Image, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(_content, BitmapScalingMode.HighQuality);
                break;
            case StampKind.Whiteout:
                _content = new Rectangle
                {
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    StrokeThickness = 0.5,
                };
                break;
            default:
                _textBlock = new TextBlock
                {
                    Padding = new Thickness(PdfSaver.TextPadding),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextWrapping = TextWrapping.NoWrap,
                };
                _content = _textBlock;
                break;
        }
        _content.Cursor = Cursors.SizeAll;
        Children.Add(_content);

        var accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        _outline = new Rectangle { Stroke = accent, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
        _resizeHandle = new Rectangle { Fill = Brushes.White, Stroke = accent, Cursor = Cursors.SizeNWSE, ToolTip = "Drag to resize" };
        _rotateHandle = new Ellipse { Fill = accent, Stroke = Brushes.White, Cursor = Cursors.Hand, ToolTip = "Drag to rotate (hold Shift to snap to 15°)" };
        _rotateArm = new Line { Stroke = accent, IsHitTestVisible = false };
        _chrome.Children.Add(_outline);
        _chrome.Children.Add(_rotateArm);
        _chrome.Children.Add(_resizeHandle);
        _chrome.Children.Add(_rotateHandle);
        _chrome.Visibility = Visibility.Collapsed;
        Children.Add(_chrome);

        _content.MouseLeftButtonDown += OnContentDown;
        _resizeHandle.MouseLeftButtonDown += (s, e) => BeginGesture(Gesture.Resize, e, _resizeHandle);
        _rotateHandle.MouseLeftButtonDown += (s, e) => BeginGesture(Gesture.Rotate, e, _rotateHandle);
        foreach (var el in new UIElement[] { _content, _resizeHandle, _rotateHandle })
        {
            el.MouseMove += OnGestureMove;
            el.MouseLeftButtonUp += OnGestureUp;
            el.LostMouseCapture += (s, e) => EndGesture();
        }

        model.PropertyChanged += OnModelChanged;
        if (model.Kind == StampKind.Text) ApplyTextStyle();
        SyncFromModel();
    }

    public StampModel Model { get; }

    public event Action<StampView>? SelectRequested;
    public event Action<StampView>? GestureStarted;
    public event Action<StampView>? GestureEnded;
    public event Action<StampView>? EmptyTextCommitted;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            _chrome.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Panel.SetZIndex(this, value ? 10 : 0);
            if (!value) CommitEdit();
        }
    }

    public bool IsEditing => _editor != null;

    /// <summary>Screen pixels per canvas unit, so handles stay a constant on-screen size.</summary>
    public void SetUiScale(double pixelsPerUnit)
    {
        _uiScale = pixelsPerUnit > 0 ? pixelsPerUnit : 1;
        LayoutChrome();
    }

    // ------------------------------------------------------------------ model sync

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Model.Kind == StampKind.Text && e.PropertyName is nameof(StampModel.Text) or nameof(StampModel.FontSize)
                or nameof(StampModel.FontFamily) or nameof(StampModel.Color))
        {
            ApplyTextStyle();
            if (e.PropertyName != nameof(StampModel.Color)) ResizeToText();
        }
        SyncFromModel();
    }

    private void SyncFromModel()
    {
        Width = Model.Width;
        Height = Model.Height;
        Canvas.SetLeft(this, Model.CenterX - Model.Width / 2);
        Canvas.SetTop(this, Model.CenterY - Model.Height / 2);
        _rotate.Angle = Model.Angle;
        LayoutChrome();
    }

    private void ApplyTextStyle()
    {
        if (_textBlock == null) return;
        var family = new FontFamily(FontPicker.PickFor(Model.Text, Model.FontFamily));
        _textBlock.Text = Model.Text;
        _textBlock.FontFamily = family;
        _textBlock.FontSize = Model.FontSize;
        _textBlock.LineHeight = Model.FontSize * PdfSaver.LineSpacing;
        _textBlock.Foreground = new SolidColorBrush(Model.Color);
        if (_editor != null)
        {
            _editor.FontFamily = family;
            _editor.FontSize = Model.FontSize;
            _editor.Foreground = _textBlock.Foreground;
        }
    }

    public static Size MeasureText(StampModel m)
    {
        var family = FontPicker.PickFor(m.Text, m.FontFamily);
        var typeface = new Typeface(family);
        var lines = m.Text.Replace("\r\n", "\n").Split('\n');
        double w = 0;
        foreach (var line in lines)
        {
            var ft = new FormattedText(line.Length == 0 ? " " : line, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, m.FontSize, Brushes.Black, 1.0);
            w = Math.Max(w, ft.WidthIncludingTrailingWhitespace);
        }
        double pad = 2 * PdfSaver.TextPadding;
        return new Size(Math.Max(w, m.FontSize * 0.6) + pad + 1, lines.Length * m.FontSize * PdfSaver.LineSpacing + pad);
    }

    /// <summary>Recomputes the text box size, keeping its top-left corner fixed in the rotated frame.</summary>
    private void ResizeToText()
    {
        var size = MeasureText(Model);
        double dw = size.Width - Model.Width, dh = size.Height - Model.Height;
        if (Math.Abs(dw) < 0.01 && Math.Abs(dh) < 0.01) return;
        var shift = Rotate(new Vector(dw / 2, dh / 2), Model.Angle);
        Model.Width = size.Width;
        Model.Height = size.Height;
        Model.CenterX += shift.X;
        Model.CenterY += shift.Y;
    }

    private void LayoutChrome()
    {
        double u = 1 / _uiScale; // canvas units per screen pixel
        double w = Model.Width, h = Model.Height;
        double hs = HandlePixels * u;

        _outline.StrokeThickness = 1 * u;
        _outline.StrokeDashArray = new DoubleCollection { 4, 3 };
        _outline.Width = w + 4 * u;
        _outline.Height = h + 4 * u;
        Canvas.SetLeft(_outline, -2 * u);
        Canvas.SetTop(_outline, -2 * u);

        _resizeHandle.Width = _resizeHandle.Height = hs;
        _resizeHandle.StrokeThickness = 1.2 * u;
        Canvas.SetLeft(_resizeHandle, w - hs / 2);
        Canvas.SetTop(_resizeHandle, h - hs / 2);

        double arm = RotateArmPixels * u;
        _rotateArm.X1 = _rotateArm.X2 = w / 2;
        _rotateArm.Y1 = -2 * u;
        _rotateArm.Y2 = -arm;
        _rotateArm.StrokeThickness = 1 * u;
        _rotateHandle.Width = _rotateHandle.Height = hs * 1.2;
        _rotateHandle.StrokeThickness = 1.2 * u;
        Canvas.SetLeft(_rotateHandle, w / 2 - hs * 0.6);
        Canvas.SetTop(_rotateHandle, -arm - hs * 0.6);

        if (_editor != null)
        {
            _editor.MinWidth = w;
            _editor.MinHeight = h;
        }
    }

    // ------------------------------------------------------------------ gestures

    private Canvas? Layer => Parent as Canvas;

    private void OnContentDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor != null) return;
        if (e.ClickCount == 2 && Model.Kind == StampKind.Text)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }
        BeginGesture(Gesture.Move, e, _content);
    }

    private void BeginGesture(Gesture g, MouseButtonEventArgs e, UIElement captureOn)
    {
        if (Layer == null) return;
        SelectRequested?.Invoke(this);
        GestureStarted?.Invoke(this);
        _gesture = g;
        _startPointer = e.GetPosition(Layer);
        _startCx = Model.CenterX;
        _startCy = Model.CenterY;
        _startW = Model.Width;
        _startH = Model.Height;
        _startFont = Model.FontSize;
        // Top-left corner (in the rotated frame) stays put while resizing.
        _anchor = new Point(_startCx, _startCy) + Rotate(new Vector(-_startW / 2, -_startH / 2), Model.Angle);
        captureOn.CaptureMouse();
        e.Handled = true;
    }

    private void OnGestureMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None || Layer == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Layer);
        switch (_gesture)
        {
            case Gesture.Move:
                Model.CenterX = _startCx + (p.X - _startPointer.X);
                Model.CenterY = _startCy + (p.Y - _startPointer.Y);
                break;

            case Gesture.Resize:
            {
                var local = Rotate(p - _anchor, -Model.Angle);
                double nw, nh;
                if (Model.Kind == StampKind.Whiteout)
                {
                    nw = Math.Max(4, local.X);
                    nh = Math.Max(4, local.Y);
                }
                else
                {
                    // Keep the aspect ratio: project the pointer onto the box diagonal.
                    double s = (local.X * _startW + local.Y * _startH) / (_startW * _startW + _startH * _startH);
                    s = Math.Max(s, 6 / Math.Min(_startW, _startH));
                    nw = _startW * s;
                    nh = _startH * s;
                    if (Model.Kind == StampKind.Text)
                    {
                        Model.FontSize = _startFont * s; // size follows from the text measurement
                        var c = _anchor + Rotate(new Vector(Model.Width / 2, Model.Height / 2), Model.Angle);
                        Model.CenterX = c.X;
                        Model.CenterY = c.Y;
                        break;
                    }
                }
                Model.Width = nw;
                Model.Height = nh;
                var center = _anchor + Rotate(new Vector(nw / 2, nh / 2), Model.Angle);
                Model.CenterX = center.X;
                Model.CenterY = center.Y;
                break;
            }

            case Gesture.Rotate:
            {
                var v = p - new Point(Model.CenterX, Model.CenterY);
                if (v.Length < 1) break;
                double angle = Math.Atan2(v.X, -v.Y) * 180 / Math.PI;
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                double step = shift ? 15 : 90;
                double snapped = Math.Round(angle / step) * step;
                if (shift || Math.Abs(snapped - angle) < 4) angle = snapped;
                Model.Angle = angle;
                break;
            }
        }
        e.Handled = true;
    }

    private void OnGestureUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        ((UIElement)sender).ReleaseMouseCapture();
        EndGesture();
        e.Handled = true;
    }

    private void EndGesture()
    {
        if (_gesture == Gesture.None) return;
        _gesture = Gesture.None;
        GestureEnded?.Invoke(this);
    }

    // ------------------------------------------------------------------ text editing

    public void BeginEdit()
    {
        if (Model.Kind != StampKind.Text || _editor != null || _textBlock == null) return;
        GestureStarted?.Invoke(this);
        _editor = new TextBox
        {
            Text = Model.Text,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x25, 0x63, 0xEB)),
            FontFamily = _textBlock.FontFamily,
            FontSize = Model.FontSize,
            Foreground = _textBlock.Foreground,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _editor.TextChanged += (s, e) => { if (_editor != null) Model.Text = _editor.Text; };
        _editor.LostKeyboardFocus += (s, e) => CommitEdit();
        _editor.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                CommitEdit();
                e.Handled = true;
            }
        };
        _textBlock.Visibility = Visibility.Hidden;
        Children.Add(_editor);
        LayoutChrome();
        Dispatcher.BeginInvoke(() =>
        {
            if (_editor == null) return;
            _editor.Focus();
            Keyboard.Focus(_editor);
            _editor.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void CommitEdit()
    {
        if (_editor == null || _textBlock == null) return;
        var editor = _editor;
        _editor = null;
        Model.Text = editor.Text.TrimEnd();
        Children.Remove(editor);
        _textBlock.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(Model.Text)) EmptyTextCommitted?.Invoke(this);
        else GestureEnded?.Invoke(this);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Rotates a vector clockwise (screen coordinates, y down) by the given degrees.</summary>
    public static Vector Rotate(Vector v, double degrees)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
