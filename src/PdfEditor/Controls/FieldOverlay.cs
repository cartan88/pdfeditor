using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PdfEditor.Models;
using PdfEditor.Services;

namespace PdfEditor.Controls;

/// <summary>Creates live WPF input controls on top of a page for each AcroForm widget.</summary>
public static class FieldOverlay
{
    private static readonly Brush FieldTint = Frozen(Color.FromArgb(0x55, 0x9E, 0xC5, 0xFF));
    private static readonly Brush FieldOpaque = Frozen(Color.FromRgb(0xE6, 0xF0, 0xFF));
    private static readonly Brush FocusBrush = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));

    public static bool Highlight { get; set; } = true;

    /// <summary>
    /// Unsubscribe actions for the model events each page's controls listen to. The field models outlive the
    /// controls (overlays are rebuilt when highlighting is toggled), so without this every rebuild would leave the
    /// old controls alive and still reacting to edits.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PageView, List<Action>> Subscriptions = new();

    /// <param name="onSignatureField">Called when a signature field is clicked, with its rectangle in view space.</param>
    public static void Build(PageView page, IEnumerable<WidgetModel> widgets, Action<WidgetModel, Rect> onSignatureField)
    {
        var subscriptions = Subscriptions.GetOrCreateValue(page);
        foreach (var unsubscribe in subscriptions) unsubscribe();
        subscriptions.Clear();
        page.FieldLayer.Children.Clear();
        // Text in the saved file isn't kerned; without this, pairs such as "Ta" sit closer on screen (inherited by all controls).
        System.Windows.Documents.Typography.SetKerning(page.FieldLayer, false);
        // Controls are added in reading order: WPF tabs through equal-TabIndex controls in the order they were added.
        foreach (var (w, r) in TabOrder(widgets.Where(w => !w.Hidden), page.Geometry))
        {
            if (r.Width < 2 || r.Height < 2) continue;
            FrameworkElement? el = w.Field.Kind switch
            {
                FieldKind.Text => CreateText(w, r, subscriptions),
                FieldKind.CheckBox or FieldKind.Radio => CreateToggle(w, r, subscriptions),
                FieldKind.Choice => CreateChoice(w, r, subscriptions),
                FieldKind.Signature => CreateSignature(w, r, onSignatureField),
                _ => null,
            };
            if (el == null) continue;
            el.Width = r.Width;
            el.Height = r.Height;
            el.ToolTip ??= w.Field.FullName;
            Canvas.SetLeft(el, r.X);
            Canvas.SetTop(el, r.Y);
            page.FieldLayer.Children.Add(el);
        }
    }

    /// <summary>
    /// Sorts widgets into the order the user reads the page (after rotation): rows top to bottom and
    /// left to right within a row, or columns left to right and top to bottom when the page asks for it.
    /// A widget joins the current row when its top edge is above the middle of both it and the row's first widget,
    /// so fields that are only roughly aligned (a radio beside a text box) still share a row.
    /// </summary>
    public static List<(WidgetModel Widget, Rect ViewRect)> TabOrder(IEnumerable<WidgetModel> widgets, PageGeometry geometry)
    {
        bool columns = geometry.ColumnTabOrder;
        // Work in "primary/secondary" coordinates so one algorithm handles rows and columns.
        double Start(Rect r) => columns ? r.Left : r.Top;
        double Size(Rect r) => columns ? r.Width : r.Height;
        double Cross(Rect r) => columns ? r.Top : r.Left;

        var items = widgets.Select(w => (Widget: w, ViewRect: geometry.PdfRectToView(w.PdfRect)))
            .OrderBy(x => Start(x.ViewRect)).ThenBy(x => Cross(x.ViewRect)).ToList();

        var result = new List<(WidgetModel, Rect)>(items.Count);
        int i = 0;
        while (i < items.Count)
        {
            var first = items[i].ViewRect;
            int end = i + 1;
            while (end < items.Count
                   && Start(items[end].ViewRect) < Start(first) + Math.Min(Size(first), Size(items[end].ViewRect)) / 2)
                end++;
            result.AddRange(items.Skip(i).Take(end - i).OrderBy(x => Cross(x.ViewRect)));
            i = end;
        }
        return result;
    }

    /// <summary>
    /// Empty fields get a translucent tint so the form's own box shows through; fields that already
    /// had a value need an opaque background to hide the value printed in the page image.
    /// </summary>
    private static Brush BackgroundFor(bool initiallyEmpty) =>
        initiallyEmpty ? (Highlight ? FieldTint : Brushes.Transparent) : (Highlight ? FieldOpaque : Brushes.White);

    /// <summary>
    /// WPF's TextBox and PasswordBox templates keep their text this far inside the left and right edges.
    /// Subtracting it from the padding puts the text exactly <see cref="FieldText.Padding"/> from the edge, as in the saved file.
    /// </summary>
    private const double TextBoxInset = 2;

    private static Thickness TextPadding(bool multiline) =>
        new(FieldText.Padding - TextBoxInset, multiline ? FieldText.Padding : 0, FieldText.Padding - TextBoxInset, 0);

    private static FrameworkElement CreateText(WidgetModel w, Rect r, List<Action> subscriptions)
    {
        var f = w.Field;
        if (f.Comb && f.MaxLength > 0 && !f.Multiline) return CreateComb(w, r);
        if (f.Password && !f.Multiline) return CreatePassword(w, r, subscriptions);

        // Font, size, colour and padding follow FieldText, the same rules PdfSaver uses for the saved appearance.
        var tb = new TextBox
        {
            BorderThickness = new Thickness(0),
            Padding = TextPadding(f.Multiline),
            Background = BackgroundFor(string.IsNullOrEmpty(f.Value)),
            Foreground = Frozen(f.TextColor),
            VerticalContentAlignment = f.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = f.Multiline,
            TextWrapping = f.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsReadOnly = f.ReadOnly,
            TextAlignment = f.Alignment switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
            CaretBrush = Brushes.Black,
        };
        if (f.MaxLength > 0) tb.MaxLength = f.MaxLength;
        tb.SetBinding(TextBox.TextProperty, new Binding(nameof(FormFieldModel.Value))
        {
            Source = f,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        // Auto-sized fields shrink as the text grows, exactly as they will in the saved file.
        void Fit()
        {
            string family = FieldText.Family(f, tb.Text);
            if (tb.FontFamily.Source != family) tb.FontFamily = new FontFamily(family);
            tb.FontSize = f.Multiline ? FieldText.MultilineSize(f) : FieldText.SingleLineSize(f, tb.Text, r.Width, r.Height);
        }
        Fit();
        tb.TextChanged += (s, e) => Fit();
        AddFocusRing(tb);
        return tb;
    }

    /// <summary>Password fields show asterisks, as the saved appearance does.</summary>
    private static FrameworkElement CreatePassword(WidgetModel w, Rect r, List<Action> subscriptions)
    {
        var f = w.Field;
        var pb = new PasswordBox
        {
            PasswordChar = '*',
            BorderThickness = new Thickness(0),
            Padding = TextPadding(false),
            Background = BackgroundFor(string.IsNullOrEmpty(f.Value)),
            Foreground = Frozen(f.TextColor),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = !f.ReadOnly,
            Password = f.Value,
        };
        if (f.MaxLength > 0) pb.MaxLength = f.MaxLength;
        void Fit()
        {
            string shown = FieldText.Display(f, pb.Password);
            pb.FontFamily = new FontFamily(FieldText.Family(f, shown));
            pb.FontSize = FieldText.SingleLineSize(f, shown, r.Width, r.Height);
        }
        Fit();
        bool syncing = false;
        pb.PasswordChanged += (s, e) =>
        {
            Fit();
            if (syncing) return;
            syncing = true;
            f.Value = pb.Password;
            syncing = false;
        };
        Listen(f, subscriptions, () =>
        {
            if (syncing || pb.Password == f.Value) return;
            syncing = true;
            pb.Password = f.Value; // undo / redo
            syncing = false;
            Fit();
        });
        AddFocusRing(pb);
        return pb;
    }

    /// <summary>
    /// Comb fields: one character centred in each cell, as in the saved file. A TextBox with invisible text takes
    /// the typing; <see cref="CombCells"/> draws the characters and highlights the cell being typed into.
    /// </summary>
    private static FrameworkElement CreateComb(WidgetModel w, Rect r)
    {
        var f = w.Field;
        var tb = new TextBox
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            CaretBrush = Brushes.Transparent,
            SelectionOpacity = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = f.ReadOnly,
            MaxLength = f.MaxLength,
            FontSize = FieldText.CombSize(f, r.Height),
        };
        tb.SetBinding(TextBox.TextProperty, new Binding(nameof(FormFieldModel.Value))
        {
            Source = f,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        var cells = new CombCells(f, tb, r.Width, r.Height) { IsHitTestVisible = false };
        tb.TextChanged += (s, e) => cells.InvalidateVisual();
        tb.SelectionChanged += (s, e) => cells.InvalidateVisual();
        tb.GotKeyboardFocus += (s, e) => cells.InvalidateVisual();
        tb.LostKeyboardFocus += (s, e) => cells.InvalidateVisual();
        AddFocusRing(tb);
        var grid = new Grid { Background = BackgroundFor(string.IsNullOrEmpty(f.Value)), ToolTip = f.FullName };
        grid.Children.Add(cells);
        grid.Children.Add(tb);
        return grid;
    }

    /// <summary>Draws a comb field's characters (one per cell, centred on the shared baseline) and the active cell.</summary>
    private sealed class CombCells(FormFieldModel field, TextBox input, double width, double height) : FrameworkElement
    {
        private static readonly Brush ActiveCell = Frozen(Color.FromArgb(0x40, 0x25, 0x63, 0xEB));

        protected override void OnRender(DrawingContext dc)
        {
            string text = input.Text;
            double cell = width / field.MaxLength, size = FieldText.CombSize(field, height);
            string family = FieldText.Family(field, text);
            var typeface = new Typeface(family);
            var brush = Frozen(field.TextColor);
            double baseline = FieldText.CenteredBaseline(family, size, height);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (input.IsKeyboardFocused)
            {
                int active = Math.Min(input.CaretIndex, field.MaxLength - 1);
                dc.DrawRectangle(ActiveCell, null, new Rect(active * cell, 0, cell, height));
            }
            for (int i = 0; i < Math.Min(text.Length, field.MaxLength); i++)
            {
                var ft = new FormattedText(text[i].ToString(), System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
                double x = i * cell + (cell - FieldText.Width(text[i].ToString(), family, size)) / 2;
                dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
            }
        }
    }

    /// <summary>Subscribes to a field's changes and records how to undo it when the page is rebuilt.</summary>
    private static void Listen(FormFieldModel field, List<Action> subscriptions, Action onChanged)
    {
        PropertyChangedEventHandler handler = (s, e) => onChanged();
        field.PropertyChanged += handler;
        subscriptions.Add(() => field.PropertyChanged -= handler);
    }

    private static FrameworkElement CreateToggle(WidgetModel w, Rect r, List<Action> subscriptions)
    {
        var f = w.Field;
        bool radio = f.Kind == FieldKind.Radio;
        var glyph = new TextBlock
        {
            Text = radio ? "●" : "✔",
            FontFamily = new FontFamily("Segoe UI Symbol"),
            Foreground = Brushes.Black,
        };
        var border = new Border
        {
            Background = BackgroundFor(f.Value == "Off" || string.IsNullOrEmpty(f.Value)),
            Cursor = f.ReadOnly ? Cursors.Arrow : Cursors.Hand,
            CornerRadius = new CornerRadius(radio ? Math.Min(r.Width, r.Height) / 2 : 0),
            Child = new Viewbox { Child = glyph, Margin = new Thickness(Math.Min(r.Width, r.Height) * 0.15) },
            Focusable = true,
        };
        void Update() => glyph.Visibility = f.Value == w.OnState ? Visibility.Visible : Visibility.Hidden;
        Update();
        Listen(f, subscriptions, Update);

        void Toggle()
        {
            if (f.ReadOnly) return;
            f.Value = radio ? w.OnState : (f.Value == w.OnState ? "Off" : w.OnState);
        }
        border.MouseLeftButtonDown += (s, e) => { Toggle(); border.Focus(); e.Handled = true; };
        border.KeyDown += (s, e) => { if (e.Key == Key.Space) { Toggle(); e.Handled = true; } };
        AddFocusRing(border);
        return border;
    }

    private static string ChoiceDisplay(FormFieldModel f) =>
        f.Options.FirstOrDefault(o => o.Export == f.Value).Display ?? f.Value;

    private static FrameworkElement CreateChoice(WidgetModel w, Rect r, List<Action> subscriptions)
    {
        var f = w.Field;
        var cb = new ComboBox
        {
            IsEditable = f.Editable || f.Options.Count == 0,
            IsEnabled = !f.ReadOnly,
            // Same font, size and colour rules as the saved appearance (the drop-down arrow still takes some room).
            FontFamily = new FontFamily(FieldText.Family(f, ChoiceDisplay(f))),
            FontSize = FieldText.SingleLineSize(f, ChoiceDisplay(f), r.Width, r.Height),
            Foreground = Frozen(f.TextColor),
            Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BackgroundFor(string.IsNullOrEmpty(f.Value)),
            BorderThickness = new Thickness(0),
            ItemsSource = f.Options.Select(o => o.Display).ToList(),
        };
        bool syncing = false;
        void Sync()
        {
            syncing = true;
            int idx = f.Options.FindIndex(o => o.Export == f.Value);
            cb.SelectedIndex = idx;
            if (idx < 0 && cb.IsEditable) cb.Text = f.Value;
            syncing = false;
        }
        Sync();
        Listen(f, subscriptions, () => { if (!syncing) Sync(); });
        cb.SelectionChanged += (s, e) =>
        {
            if (syncing || cb.SelectedIndex < 0) return;
            syncing = true;
            f.Value = f.Options[cb.SelectedIndex].Export;
            syncing = false;
        };
        if (cb.IsEditable)
        {
            cb.AddHandler(TextBoxBase_TextChanged, new TextChangedEventHandler((s, e) =>
            {
                if (syncing) return;
                int idx = f.Options.FindIndex(o => o.Display == cb.Text);
                syncing = true;
                f.Value = idx >= 0 ? f.Options[idx].Export : cb.Text;
                syncing = false;
            }));
        }
        return cb;
    }

    private static readonly RoutedEvent TextBoxBase_TextChanged = System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent;

    private static FrameworkElement CreateSignature(WidgetModel w, Rect r, Action<WidgetModel, Rect> onClick)
    {
        bool vertical = r.Height > r.Width * 1.3;
        var label = new TextBlock
        {
            Text = "✍ Click to sign",
            Foreground = FocusBrush,
            FontSize = Math.Clamp(Math.Min(vertical ? r.Width : r.Height, vertical ? r.Height : r.Width) * 0.45, 5, 11),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = vertical ? new RotateTransform(-90) : Transform.Identity,
        };
        var border = new Border
        {
            Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xD5, 0x4F)),
            BorderBrush = Frozen(Color.FromRgb(0xE0, 0xA8, 0x00)),
            BorderThickness = new Thickness(0.75),
            Cursor = Cursors.Hand,
            Child = label,
            ToolTip = $"Signature field \"{w.Field.FullName}\" – click to place your signature here",
        };
        border.MouseLeftButtonDown += (s, e) => { onClick(w, r); e.Handled = true; };
        return border;
    }

    private static void AddFocusRing(Control c)
    {
        var original = c.BorderThickness;
        c.BorderBrush = FocusBrush;
        c.GotKeyboardFocus += (s, e) => c.BorderThickness = new Thickness(1);
        c.LostKeyboardFocus += (s, e) => c.BorderThickness = original;
    }

    private static void AddFocusRing(Border b)
    {
        b.BorderBrush = FocusBrush;
        b.GotKeyboardFocus += (s, e) => b.BorderThickness = new Thickness(1);
        b.LostKeyboardFocus += (s, e) => b.BorderThickness = new Thickness(0);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
