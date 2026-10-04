using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PdfEditor.Models;

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
        // Controls are added in reading order: WPF tabs through equal-TabIndex controls in the order they were added.
        foreach (var (w, r) in TabOrder(widgets.Where(w => !w.Hidden), page.Geometry))
        {
            if (r.Width < 2 || r.Height < 2) continue;
            FrameworkElement? el = w.Field.Kind switch
            {
                FieldKind.Text => CreateText(w, r),
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

    private static FrameworkElement CreateText(WidgetModel w, Rect r)
    {
        var f = w.Field;
        double fs = f.FontSize > 0 ? f.FontSize : f.Multiline ? 10 : Math.Clamp(r.Height * 0.65, 6, 12);
        var tb = new TextBox
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(1, 0, 1, 0),
            Background = BackgroundFor(string.IsNullOrEmpty(f.Value)),
            FontFamily = new FontFamily("Arial"),
            FontSize = fs,
            VerticalContentAlignment = f.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = f.Multiline,
            TextWrapping = f.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsReadOnly = f.ReadOnly,
            TextAlignment = f.Alignment switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
            CaretBrush = Brushes.Black,
        };
        if (f.MaxLength > 0) tb.MaxLength = f.MaxLength;
        if (f.Comb && f.MaxLength > 0)
        {
            // Spread characters roughly across the comb cells.
            tb.FontFamily = new FontFamily("Consolas");
            tb.TextAlignment = TextAlignment.Left;
        }
        tb.SetBinding(TextBox.TextProperty, new Binding(nameof(FormFieldModel.Value))
        {
            Source = f,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        AddFocusRing(tb);
        return tb;
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

    private static FrameworkElement CreateChoice(WidgetModel w, Rect r, List<Action> subscriptions)
    {
        var f = w.Field;
        var cb = new ComboBox
        {
            IsEditable = f.Editable || f.Options.Count == 0,
            IsEnabled = !f.ReadOnly,
            FontSize = f.FontSize > 0 ? f.FontSize : Math.Clamp(r.Height * 0.6, 6, 12),
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
