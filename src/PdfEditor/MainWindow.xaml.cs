using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfEditor.Controls;
using PdfEditor.Dialogs;
using PdfEditor.Models;
using PdfEditor.Services;

namespace PdfEditor;

public partial class MainWindow : Window
{
    private const double PointsToDip = 96.0 / 72.0;
    /// <summary>Largest full-page bitmap kept per page (8 MP = 32 MB); higher zoom uses a detail bitmap of the visible area.</summary>
    private const double MaxPagePixels = 8_000_000;
    private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };
    private static readonly (string Name, Color Color)[] InkColors =
    {
        ("Black", Colors.Black), ("Blue", Color.FromRgb(0x1A, 0x3A, 0x9C)), ("Red", Color.FromRgb(0xC0, 0x1C, 0x1C)), ("Green", Color.FromRgb(0x16, 0x6B, 0x34)),
    };

    private enum Tool { Select, Text, Date, Check, Cross, Whiteout, PlaceImage }

    private record StampState(StampKind Kind, int Page, double Cx, double Cy, double W, double H, double Angle,
        string Text, double FontSize, Color Color, string Font, bool Bold, bool Italic, byte[]? Data);

    /// <summary>Everything undo covers: placed items, and form field values in <see cref="_fields"/> order.</summary>
    private sealed record EditorState(List<StampState> Stamps, string[] Fields)
    {
        public bool SameAs(EditorState other) => Stamps.SequenceEqual(other.Stamps) && Fields.SequenceEqual(other.Fields);
    }

    // Document state
    private string? _path;
    private byte[]? _bytes;
    private string? _password;
    private PdfRenderer? _renderer;
    private readonly List<PageView> _pages = new();
    private List<FormFieldModel> _fields = new();
    private readonly List<StampView> _stamps = new();
    private bool _dirty;
    private bool _busy;
    private bool _signedWarningAccepted;

    // View state
    private readonly ScaleTransform _zoomTransform = new(PointsToDip, PointsToDip);
    private double _zoom = 1;
    private bool _fitWidth = true;
    private readonly DispatcherTimer _renderTimer;
    private int _renderGeneration;
    private bool _renderLoopRunning;

    // Editing state
    private Tool _tool = Tool.Select;
    private byte[]? _pendingImage;
    private bool _pendingIsSignature;
    private StampView? _selected;
    private double _textFontSize = 12;
    private Color _textColor = Colors.Black;
    // Font for new text; remembered between sessions in UserSettings.
    private string _textFont = "Arial";
    private bool _textBold, _textItalic;
    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();
    private EditorState? _gestureSnapshot;
    // Form field undo: last value seen per field (to rebuild the "before" state), the text field currently
    // being typed into (its keystrokes merge into one step), and a guard while undo/redo writes values back.
    private Dictionary<FormFieldModel, int> _fieldIndex = new(ReferenceEqualityComparer.Instance);
    private string[] _fieldValues = Array.Empty<string>();
    private FormFieldModel? _typingField;
    private bool _restoring;
    private bool _updatingSelectionBar;

    // White-out drag
    private StampView? _drawing;
    private Point _drawStart;

    public MainWindow()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (s, e) =>
        {
            _renderTimer!.Stop();
            _ = RenderVisiblePagesAsync();
        }, Dispatcher);
        _renderTimer.Stop();

        foreach (var s in new[] { 6, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 }) FontSizeBox.Items.Add(s.ToString());
        foreach (var (name, color) in InkColors)
        {
            var item = new ComboBoxItem { Tag = color };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border { Width = 12, Height = 12, Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(2) });
            sp.Children.Add(new TextBlock { Text = name });
            item.Content = sp;
            ColorBox.Items.Add(item);
        }
        var prefs = UserSettings.Load();
        (_textFont, _textBold, _textItalic) = (prefs.TextFont, prefs.TextBold, prefs.TextItalic);
        FillFontBox();
        SizeChanged += (s, e) => { if (_fitWidth) FitWidth(); };
        // Leaving a form field ends its typing session, so the next edit starts a new undo step.
        PagesPanel.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) => _typingField = null), true);
        UpdateUi();
        UpdateSelectionBar();
    }

    // =====================================================================================
    // Opening
    // =====================================================================================

    public async Task OpenAsync(string path)
    {
        if (!ConfirmDiscardChanges()) return;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            ShowError("Could not read the file.", ex);
            return;
        }

        string? password = null;
        PdfSharp.Pdf.PdfDocument? doc = null;
        while (doc == null)
        {
            try
            {
                doc = PdfOpen.Open(bytes, password);
            }
            catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                string msg = password == null
                    ? $"\"{Path.GetFileName(path)}\" is protected. Enter the document password:"
                    : "That password did not work. Note: editing a restricted PDF requires its owner (permissions) password.";
                var dlg = new PasswordDialog(msg) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                password = dlg.Password;
            }
            catch (Exception ex)
            {
                ShowError("This file could not be opened as a PDF.", ex);
                return;
            }
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            // Items placed by an earlier save come back as editable objects; the page itself is shown without them.
            var restored = await Task.Run(() => StampStore.Extract(bytes, password));
            if (restored.PagesRestored > 0)
            {
                bytes = restored.Bytes;
                doc = PdfOpen.Open(bytes, password);
            }
            var geometry = PdfOpen.ReadGeometry(doc);
            var fields = FormReader.Read(doc).Select(b => b.Model).ToList();
            var renderer = await PdfRenderer.LoadAsync(bytes, password);

            CloseDocument();
            _path = path;
            _bytes = bytes;
            _password = password;
            _renderer = renderer;
            _fields = fields;
            _fieldIndex = new Dictionary<FormFieldModel, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < _fields.Count; i++) _fieldIndex[_fields[i]] = i;
            _fieldValues = _fields.Select(f => f.Value).ToArray();
            foreach (var f in _fields) f.PropertyChanged += (s, e) => OnFieldChanged((FormFieldModel)s!);

            foreach (var g in geometry)
            {
                var pv = new PageView(g, _zoomTransform);
                pv.Surface.MouseLeftButtonDown += (s, e) => Page_MouseDown(pv, e);
                pv.Surface.MouseMove += (s, e) => Page_MouseMove(pv, e);
                pv.Surface.MouseLeftButtonUp += (s, e) => Page_MouseUp(pv, e);
                _pages.Add(pv);
                PagesPanel.Children.Add(pv);
            }
            BuildFieldOverlays();
            foreach (var stamp in restored.Stamps.Where(s => s.PageIndex < _pages.Count)) AddStamp(stamp, select: false);

            _dirty = false;
            _fitWidth = true;
            UpdateLayout();
            FitWidth();
            Scroller.ScrollToHome();
            int fillable = _fields.Count(f => f.Kind is not FieldKind.PushButton && !f.ReadOnly);
            SetStatus(fillable > 0
                ? $"Opened {Path.GetFileName(path)} – {fillable} fillable field(s). Click a field to type, or use the tools above."
                : $"Opened {Path.GetFileName(path)} – no fillable fields; use Text, Date, ✓ and Sign to fill it in.");
            if (restored.Stamps.Count > 0)
                SetStatus(StatusText.Text + $" {restored.Stamps.Count} item(s) you placed earlier can be moved and edited.");
            if (restored.PagesSkipped > 0)
                SetStatus(StatusText.Text + $" Items on {restored.PagesSkipped} page(s) were changed in another program and are now part of the page.");
            if (_fields.Any(f => f.IsSigned))
                SetStatus(StatusText.Text + " Note: this PDF is digitally signed; saving changes will invalidate that signature.");
        }
        catch (Exception ex)
        {
            ShowError("This file could not be opened.", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            UpdateUi();
        }
    }

    private void CloseDocument()
    {
        Select(null);
        SetTool(Tool.Select);
        _renderGeneration++;
        _renderer?.Dispose();
        _renderer = null;
        _pages.Clear();
        _stamps.Clear();
        PagesPanel.Children.Clear();
        _fields = new();
        _undo.Clear();
        _redo.Clear();
        _gestureSnapshot = null;
        _typingField = null;
        _fieldIndex = new(ReferenceEqualityComparer.Instance);
        _fieldValues = Array.Empty<string>();
        _path = null;
        _bytes = null;
        _dirty = false;
        _signedWarningAccepted = false;
    }

    private void BuildFieldOverlays()
    {
        var byPage = _fields.SelectMany(f => f.Widgets).GroupBy(w => w.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var page in _pages)
            FieldOverlay.Build(page, byPage.TryGetValue(page.Index, out var list) ? list : new List<WidgetModel>(), OnSignatureFieldClicked);
        FieldInfo.Text = _fields.Count > 0 ? $"{_fields.Count(f => f.Kind != FieldKind.PushButton)} form field(s)" : "";
    }

    // =====================================================================================
    // Saving
    // =====================================================================================

    private async Task<bool> SaveAsync(string path, bool flatten)
    {
        if (_bytes == null || _busy) return false;
        CommitTextEdits();
        if (!ConfirmBreakSignatures()) return false;
        var stamps = _stamps.Select(v => v.Model).ToList();
        var fields = _fields;
        var bytes = _bytes;
        var password = _password;
        try
        {
            _busy = true;
            Mouse.OverrideCursor = Cursors.Wait;
            SetStatus("Saving…");
            await Task.Run(() => PdfSaver.Save(bytes, password, fields, stamps, flatten, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("The file could not be written. If it is open in another program (such as a PDF viewer) or read-only, close it there or use Save As.", ex);
            return false;
        }
        catch (Exception ex)
        {
            ShowError("Saving failed.", ex);
            return false;
        }
        finally
        {
            _busy = false;
            Mouse.OverrideCursor = null;
        }

        if (!flatten)
        {
            _path = path;
            _dirty = false;
        }
        SetStatus($"Saved {(flatten ? "flattened copy " : "")}to {path}");
        UpdateUi();
        return true;
    }

    /// <summary>
    /// Saving rewrites the whole file, which invalidates any existing certificate-based signatures.
    /// Asks once per document before doing that.
    /// </summary>
    private bool ConfirmBreakSignatures()
    {
        if (_signedWarningAccepted || !_fields.Any(f => f.IsSigned)) return true;
        var r = MessageBox.Show(this,
            "This PDF contains a digital (certificate-based) signature.\n\n" +
            "Saving changes will make that signature invalid – viewers such as Acrobat will report the document as modified or the signature as broken.\n\n" +
            "Save anyway?",
            "PDF Editor", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        _signedWarningAccepted = r == MessageBoxResult.Yes;
        return _signedWarningAccepted;
    }

    private Task<bool> SaveCurrentAsync() =>
        _path != null ? SaveAsync(_path, false) : SaveAsAsync(false);

    private async Task<bool> SaveAsAsync(bool flatten)
    {
        if (_bytes == null) return false;
        var name = Path.GetFileNameWithoutExtension(_path ?? "document");
        var sfd = new SaveFileDialog
        {
            Title = flatten ? "Save flattened copy" : "Save PDF as",
            Filter = "PDF files|*.pdf",
            DefaultExt = ".pdf",
            FileName = flatten ? name + " (flattened).pdf" : name + ".pdf",
            InitialDirectory = _path != null ? Path.GetDirectoryName(_path) : null,
        };
        if (sfd.ShowDialog(this) != true) return false;
        return await SaveAsync(sfd.FileName, flatten);
    }

    // =====================================================================================
    // Printing
    // =====================================================================================

    private async Task PrintAsync()
    {
        if (_bytes == null || _busy) return;
        CommitTextEdits();
        int current = CurrentPage()?.Index ?? 0;
        var dlg = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)_pages.Count,
            PageRange = new PageRange(1, _pages.Count),
        };
        if (dlg.ShowDialog() != true) return;

        List<int> pages = dlg.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => new List<int> { current },
            PageRangeSelection.UserPages => PageRangeIndices(dlg.PageRange),
            _ => Enumerable.Range(0, _pages.Count).ToList(),
        };
        if (pages.Count == 0) return;

        var stamps = _stamps.Select(v => v.Model).ToList();
        var fields = _fields;
        var bytes = _bytes;
        var password = _password;
        var geometry = _pages.Select(p => p.Geometry).ToList();
        try
        {
            _busy = true;
            Mouse.OverrideCursor = Cursors.Wait;
            SetStatus("Preparing to print…");
            // Print exactly what a flattened save would contain, so form values and placed items look final.
            var printable = await Task.Run(() => PdfSaver.Build(bytes, password, fields, stamps, flatten: true));
            await PdfPrinter.PrintAsync(printable, password, geometry, pages, dlg,
                Path.GetFileName(_path) ?? "PDF Editor document",
                (i, n) => SetStatus($"Printing page {i} of {n}…"));
            SetStatus($"Sent {pages.Count} page(s) to {dlg.PrintQueue.FullName}.");
        }
        catch (Exception ex)
        {
            SetStatus("Printing failed.");
            ShowError("Printing failed.", ex);
        }
        finally
        {
            _busy = false;
            Mouse.OverrideCursor = null;
        }
    }

    private List<int> PageRangeIndices(PageRange range)
    {
        int from = Math.Clamp(Math.Min(range.PageFrom, range.PageTo), 1, _pages.Count);
        int to = Math.Clamp(Math.Max(range.PageFrom, range.PageTo), 1, _pages.Count);
        return Enumerable.Range(from - 1, to - from + 1).ToList();
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty || _bytes == null) return true;
        var r = MessageBox.Show(this, $"Save changes to \"{Path.GetFileName(_path)}\"?", "PDF Editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.No) return true;
        // Run the save synchronously from the caller's perspective.
        var frame = new DispatcherFrame();
        bool ok = false;
        SaveCurrentAsync().ContinueWith(t => { ok = t.IsCompletedSuccessfully && t.Result; frame.Continue = false; }, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return ok;
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    // =====================================================================================
    // Rendering & zoom
    // =====================================================================================

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private async Task RenderVisiblePagesAsync()
    {
        if (_renderer == null || _renderLoopRunning) { if (_renderLoopRunning) ScheduleRender(); return; }
        _renderLoopRunning = true;
        int generation = _renderGeneration;
        try
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double viewport = Math.Max(Scroller.ViewportHeight, 200);

            // Visible pages first (by distance from the viewport), then neighbours.
            var candidates = new List<(PageView Page, double Distance)>();
            foreach (var page in _pages)
            {
                Rect bounds;
                try { bounds = page.TransformToAncestor(Scroller).TransformBounds(new Rect(page.RenderSize)); }
                catch (InvalidOperationException) { continue; }
                double distance = bounds.Bottom < 0 ? -bounds.Bottom : bounds.Top > viewport ? bounds.Top - viewport : 0;
                if (distance > viewport * 2)
                {
                    page.ClearRender();
                    continue;
                }
                if (distance > 0) page.ClearDetail();
                if (distance <= viewport) candidates.Add((page, distance));
            }

            foreach (var (page, distance) in candidates.OrderBy(c => c.Distance))
            {
                if (generation != _renderGeneration || _renderer == null) return;
                var g = page.Geometry;
                double density = PointsToDip * _zoom * dpi; // device pixels per point
                double fullWidth = g.ViewWidth * density;
                // Full-page bitmap, capped so a page never takes more than about MaxPagePixels * 4 bytes.
                int baseWidth = (int)Math.Ceiling(Math.Min(fullWidth, Math.Sqrt(MaxPagePixels * g.ViewWidth / g.ViewHeight)));
                bool needsDetail = distance == 0 && fullWidth > baseWidth * 1.05;
                try
                {
                    if (page.RenderedWidth == 0 || Math.Abs(page.RenderedWidth - baseWidth) > baseWidth * 0.15)
                    {
                        var bmp = await _renderer.RenderAsync(page.Index, baseWidth);
                        if (generation != _renderGeneration) return;
                        page.PageImage.Source = bmp;
                        page.RenderedWidth = baseWidth;
                    }
                    if (!needsDetail) page.ClearDetail();
                    else await RenderDetailAsync(page, density, generation);
                }
                catch (Exception ex)
                {
                    if (generation != _renderGeneration) return; // document was closed mid-render
                    SetStatus($"Could not render page {page.Index + 1}: {ex.Message}");
                }
            }
        }
        finally
        {
            _renderLoopRunning = false;
        }
    }

    /// <summary>
    /// When zoomed in too far for a full-page bitmap, renders just the visible part of the page (plus a margin
    /// so small scrolls don't need a new render) at full sharpness. Its size is bounded by the window, not the zoom.
    /// </summary>
    private async Task RenderDetailAsync(PageView page, double density, int generation)
    {
        var g = page.Geometry;
        var pageRect = new Rect(0, 0, g.ViewWidth, g.ViewHeight);
        Rect visible;
        try { visible = Scroller.TransformToDescendant(page.Surface).TransformBounds(new Rect(0, 0, Scroller.ViewportWidth, Scroller.ViewportHeight)); }
        catch (InvalidOperationException) { return; }
        visible.Intersect(pageRect);
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1) { page.ClearDetail(); return; }

        if (page.DetailRegion.Contains(visible) && Math.Abs(page.DetailDensity - density) <= density * 0.15) return;

        var region = visible;
        region.Inflate(visible.Width * 0.25, visible.Height * 0.25);
        region.Intersect(pageRect);
        int pixelWidth = (int)Math.Ceiling(region.Width * density);
        var fraction = new Rect(region.X / g.ViewWidth, region.Y / g.ViewHeight, region.Width / g.ViewWidth, region.Height / g.ViewHeight);
        var bmp = await _renderer!.RenderAsync(page.Index, pixelWidth, fraction);
        if (generation != _renderGeneration) return;
        page.SetDetail(bmp, region);
    }

    private void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, 0.1, 6);
        if (Math.Abs(zoom - _zoom) < 0.0001) { UpdateZoomBox(); return; }
        var a = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        double ratio = zoom / _zoom;
        double newX = (Scroller.HorizontalOffset + a.X) * ratio - a.X;
        double newY = (Scroller.VerticalOffset + a.Y) * ratio - a.Y;

        _zoom = zoom;
        _zoomTransform.ScaleX = _zoomTransform.ScaleY = zoom * PointsToDip;
        foreach (var s in _stamps) s.SetUiScale(zoom * PointsToDip);
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, newX));
        Scroller.ScrollToVerticalOffset(Math.Max(0, newY));
        UpdateZoomBox();
        ScheduleRender();
    }

    private void FitWidth()
    {
        if (_pages.Count == 0) return;
        double maxWidth = _pages.Max(p => p.Geometry.ViewWidth);
        double available = Scroller.ActualWidth - 48 - SystemParameters.VerticalScrollBarWidth - 4;
        if (available <= 50) return;
        _fitWidth = true;
        SetZoom(available / (maxWidth * PointsToDip));
    }

    private void StepZoom(int direction, Point? anchor = null)
    {
        _fitWidth = false;
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next, anchor);
    }

    private void UpdateZoomBox()
    {
        ZoomBox.SelectionChanged -= ZoomBox_SelectionChanged;
        ZoomBox.SelectedIndex = -1;
        ZoomBox.Text = $"{Math.Round(_zoom * 100)}%";
        ZoomBox.SelectionChanged += ZoomBox_SelectionChanged;
    }

    private void ApplyZoomText(string text)
    {
        if (text.StartsWith("Fit", StringComparison.OrdinalIgnoreCase)) { FitWidth(); return; }
        if (double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out var pct) && pct > 0)
        {
            _fitWidth = false;
            SetZoom(pct / 100);
        }
        else UpdateZoomBox();
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScheduleRender();
        UpdateCurrentPage();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _pages.Count == 0) return;
        StepZoom(e.Delta > 0 ? 1 : -1, e.GetPosition(Scroller));
        e.Handled = true;
    }

    private PageView? CurrentPage()
    {
        double mid = Scroller.ViewportHeight / 2;
        PageView? best = null;
        double bestDist = double.MaxValue;
        foreach (var p in _pages)
        {
            Rect b;
            try { b = p.TransformToAncestor(Scroller).TransformBounds(new Rect(p.RenderSize)); }
            catch (InvalidOperationException) { continue; }
            double d = b.Top <= mid && b.Bottom >= mid ? 0 : Math.Min(Math.Abs(b.Top - mid), Math.Abs(b.Bottom - mid));
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    private void UpdateCurrentPage()
    {
        if (PageBox.IsKeyboardFocusWithin) return;
        var p = CurrentPage();
        PageBox.Text = p != null ? (p.Index + 1).ToString() : "";
    }

    private void GoToPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        var page = _pages[index];
        var pos = page.TransformToAncestor(PagesPanel).Transform(new Point(0, 0));
        Scroller.ScrollToVerticalOffset(pos.Y + PagesPanel.Margin.Top - 8);
    }

    // =====================================================================================
    // Stamps
    // =====================================================================================

    private StampView AddStamp(StampModel model, bool select = true)
    {
        var page = _pages[model.PageIndex];
        var view = new StampView(model);
        view.SetUiScale(_zoom * PointsToDip);
        view.SelectRequested += v => Select(v);
        view.GestureStarted += v => _gestureSnapshot ??= Snapshot();
        view.GestureEnded += v => CommitGesture();
        view.EmptyTextCommitted += v =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                RemoveStamp(v);
                CommitGesture();
            });
        };
        model.PropertyChanged += (s, e) =>
        {
            MarkDirty();
            if (v_IsSelected(view)) UpdateSelectionBar();
        };
        page.StampLayer.Children.Add(view);
        _stamps.Add(view);
        if (select) Select(view);
        MarkDirty();
        return view;

        bool v_IsSelected(StampView v) => ReferenceEquals(v, _selected);
    }

    private void RemoveStamp(StampView view)
    {
        if (ReferenceEquals(view, _selected)) Select(null);
        (view.Parent as Panel)?.Children.Remove(view);
        _stamps.Remove(view);
        MarkDirty();
    }

    private void Select(StampView? view)
    {
        if (ReferenceEquals(view, _selected)) return;
        if (_selected != null) _selected.IsSelected = false;
        _selected = view;
        if (view != null)
        {
            view.IsSelected = true;
            if (Keyboard.FocusedElement is TextBox tb && tb.TemplatedParent == null && !view.IsEditing)
                Scroller.Focus(); // move focus off form fields so Delete/arrows act on the selection
        }
        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        _updatingSelectionBar = true;
        var m = _selected?.Model;
        SelectionBar.IsEnabled = m != null;
        SelectionLabel.Text = m?.Kind switch
        {
            null => "Click a placed item to rotate, resize or delete it",
            StampKind.Image => "Selected image:",
            StampKind.Whiteout => "Selected white-out:",
            _ => "Selected text:",
        };
        AngleBox.Text = m != null ? Math.Round(m.Angle, 1).ToString(CultureInfo.CurrentCulture) : "";
        bool isText = m == null || m.Kind == StampKind.Text;
        TextProps.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        double fs = m?.Kind == StampKind.Text ? m.FontSize : _textFontSize;
        FontSizeBox.Text = Math.Round(fs, 1).ToString(CultureInfo.CurrentCulture);
        SelectFont(m?.Kind == StampKind.Text ? m.FontFamily : _textFont);
        BoldButton.IsChecked = m?.Kind == StampKind.Text ? m.Bold : _textBold;
        ItalicButton.IsChecked = m?.Kind == StampKind.Text ? m.Italic : _textItalic;
        var color = m?.Kind == StampKind.Text ? m.Color : _textColor;
        ColorBox.SelectedItem = ColorBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Color)i.Tag == color);
        _updatingSelectionBar = false;
    }

    private void CommitTextEdits()
    {
        foreach (var s in _stamps.ToList()) s.CommitEdit();
    }

    private List<StampState> StampSnapshot() => _stamps.Select(v => v.Model).Select(m =>
        new StampState(m.Kind, m.PageIndex, m.CenterX, m.CenterY, m.Width, m.Height, m.Angle, m.Text, m.FontSize, m.Color, m.FontFamily, m.Bold, m.Italic, m.ImageData)).ToList();

    private EditorState Snapshot() => new(StampSnapshot(), _fields.Select(f => f.Value).ToArray());

    private void PushUndo(EditorState before)
    {
        _undo.Push(before);
        _redo.Clear();
        _typingField = null;
        UpdateUi();
    }

    /// <summary>Records an undo step if anything changed since the gesture began.</summary>
    private void CommitGesture()
    {
        var before = _gestureSnapshot;
        _gestureSnapshot = null;
        if (before != null && !before.SameAs(Snapshot())) PushUndo(before);
    }

    private void Mutate(Action action)
    {
        var before = Snapshot();
        action();
        if (!before.SameAs(Snapshot())) PushUndo(before);
    }

    /// <summary>
    /// Records an undo step for a form field edit. The field has already changed, so the "before" state is rebuilt
    /// from the last value seen. Typing into the same text field (or editable drop-down) extends the current step
    /// until focus leaves it; every check box / radio click is a step of its own.
    /// </summary>
    private void OnFieldChanged(FormFieldModel field)
    {
        MarkDirty();
        int i = _fieldIndex[field];
        if (!_restoring && !ReferenceEquals(field, _typingField))
        {
            var before = Snapshot();
            before.Fields[i] = _fieldValues[i];
            PushUndo(before);
            bool typed = field.Kind == FieldKind.Text || (field.Kind == FieldKind.Choice && (field.Editable || field.Options.Count == 0));
            if (typed) _typingField = field;
        }
        _fieldValues[i] = field.Value;
    }

    private void Restore(EditorState state)
    {
        _typingField = null;
        _restoring = true;
        try
        {
            for (int i = 0; i < _fields.Count; i++) _fields[i].Value = state.Fields[i];
        }
        finally
        {
            _restoring = false;
        }

        // Rebuild placed items only if they differ, so undoing a field edit keeps the current selection.
        if (!state.Stamps.SequenceEqual(StampSnapshot()))
        {
            Select(null);
            foreach (var v in _stamps.ToList())
                (v.Parent as Panel)?.Children.Remove(v);
            _stamps.Clear();
            foreach (var s in state.Stamps)
            {
                var m = new StampModel
                {
                    Kind = s.Kind, PageIndex = s.Page, ImageData = s.Data,
                    Text = s.Text, FontSize = s.FontSize, Color = s.Color, FontFamily = s.Font, Bold = s.Bold, Italic = s.Italic,
                };
                m.Width = s.W; m.Height = s.H; m.CenterX = s.Cx; m.CenterY = s.Cy; m.Angle = s.Angle;
                AddStamp(m, select: false);
            }
        }
        MarkDirty();
        UpdateUi();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        CommitTextEdits();
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    private void RotateSelection(double delta)
    {
        if (_selected == null) return;
        var m = _selected.Model;
        Mutate(() => m.Angle += delta);
    }

    private void DeleteSelection()
    {
        if (_selected == null) return;
        var v = _selected;
        Mutate(() => RemoveStamp(v));
    }

    private void CopySelectionToAllPages()
    {
        if (_selected == null) return;
        var src = _selected.Model;
        Mutate(() =>
        {
            foreach (var page in _pages.Where(p => p.Index != src.PageIndex))
            {
                var m = new StampModel
                {
                    Kind = src.Kind, PageIndex = page.Index, ImageData = src.ImageData, Text = src.Text,
                    FontSize = src.FontSize, Color = src.Color, FontFamily = src.FontFamily, Bold = src.Bold, Italic = src.Italic,
                };
                m.Width = src.Width; m.Height = src.Height; m.Angle = src.Angle;
                m.CenterX = Math.Clamp(src.CenterX, 0, page.Geometry.ViewWidth);
                m.CenterY = Math.Clamp(src.CenterY, 0, page.Geometry.ViewHeight);
                AddStamp(m, select: false);
            }
        });
        SetStatus($"Copied to {_pages.Count - 1} other page(s).");
    }

    // =====================================================================================
    // Tools & page interaction
    // =====================================================================================

    private void SetTool(Tool tool)
    {
        _tool = tool;
        if (tool != Tool.PlaceImage) _pendingImage = null;
        bool placing = tool != Tool.Select;
        foreach (var p in _pages)
        {
            p.FieldLayer.IsHitTestVisible = !placing;
            p.StampLayer.IsHitTestVisible = !placing;
            p.Surface.Cursor = placing ? Cursors.Cross : null;
        }
        var radio = tool switch
        {
            Tool.Text => TextTool, Tool.Date => DateTool, Tool.Check => CheckTool, Tool.Cross => CrossTool,
            Tool.Whiteout => WhiteoutTool, _ => SelectTool,
        };
        if (radio.IsChecked != true) radio.IsChecked = true;
        if (placing) Select(null);

        SetStatus(tool switch
        {
            Tool.Text => "Click on the page where the text should start. Esc to cancel.",
            Tool.Date => "Click on the page to insert today's date. Esc to cancel.",
            Tool.Check or Tool.Cross => "Click on the page to place the mark. Esc to cancel.",
            Tool.Whiteout => "Drag a rectangle over the content to hide. This only covers it visually – the text underneath can still be selected and copied. Esc to cancel.",
            Tool.PlaceImage => _pendingIsSignature
                ? "Click on the page where your signature should go (you can move, resize and rotate it afterwards). Esc to cancel."
                : "Click on the page to place the image. Esc to cancel.",
            _ => _pages.Count > 0 ? "Fill in fields directly, or drag placed items to move them. Corner handle resizes, top handle rotates." : "Ready",
        });
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var tool = sender == TextTool ? Tool.Text : sender == DateTool ? Tool.Date : sender == CheckTool ? Tool.Check
            : sender == CrossTool ? Tool.Cross : sender == WhiteoutTool ? Tool.Whiteout : Tool.Select;
        if (tool == Tool.Select && _tool == Tool.PlaceImage) return; // radio reset while placing an image
        if (tool != _tool) SetTool(tool);
    }

    private void Page_MouseDown(PageView page, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(page.Surface);
        switch (_tool)
        {
            case Tool.Select:
                if (!e.Handled)
                {
                    CommitTextEdits();
                    Select(null);
                    Scroller.Focus();
                }
                return;

            case Tool.Text:
            {
                var m = NewText(page.Index, "", _textFontSize, _textColor);
                PlaceTextAt(m, p);
                var before = Snapshot();
                var view = AddStamp(m);
                _gestureSnapshot = before;
                view.BeginEdit();
                break;
            }

            case Tool.Date:
            {
                var m = NewText(page.Index, DateTime.Today.ToString("d MMM yyyy", CultureInfo.CurrentCulture), _textFontSize, _textColor);
                PlaceTextAt(m, p);
                Mutate(() => AddStamp(m));
                break;
            }

            case Tool.Check:
            case Tool.Cross:
            {
                var m = NewText(page.Index, _tool == Tool.Check ? "✓" : "✗", Math.Max(_textFontSize, 14), _textColor);
                m.FontFamily = "Segoe UI Symbol";
                m.Bold = m.Italic = false; // marks keep their own symbol font
                var size = StampView.MeasureText(m);
                m.Width = size.Width; m.Height = size.Height;
                m.CenterX = p.X; m.CenterY = p.Y;
                Mutate(() => AddStamp(m));
                break;
            }

            case Tool.Whiteout:
            {
                _gestureSnapshot = Snapshot();
                var m = new StampModel { Kind = StampKind.Whiteout, PageIndex = page.Index };
                m.Width = 4; m.Height = 4; m.CenterX = p.X; m.CenterY = p.Y;
                _drawStart = p;
                _drawing = AddStamp(m, select: false);
                page.Surface.CaptureMouse();
                e.Handled = true;
                return; // tool switches back on mouse up
            }

            case Tool.PlaceImage when _pendingImage != null:
            {
                var img = ImageUtil.FromBytes(_pendingImage);
                double aspect = (double)img.PixelWidth / img.PixelHeight;
                double w = _pendingIsSignature ? 160 : Math.Min(220, img.PixelWidth * 0.75);
                var m = new StampModel { Kind = StampKind.Image, PageIndex = page.Index, ImageData = _pendingImage };
                m.Width = w; m.Height = w / aspect; m.CenterX = p.X; m.CenterY = p.Y;
                Mutate(() => AddStamp(m));
                break;
            }
        }
        e.Handled = true;
        SetTool(Tool.Select);
    }

    private void Page_MouseMove(PageView page, MouseEventArgs e)
    {
        if (_drawing == null) return;
        var p = e.GetPosition(page.Surface);
        var m = _drawing.Model;
        m.Width = Math.Max(2, Math.Abs(p.X - _drawStart.X));
        m.Height = Math.Max(2, Math.Abs(p.Y - _drawStart.Y));
        m.CenterX = (p.X + _drawStart.X) / 2;
        m.CenterY = (p.Y + _drawStart.Y) / 2;
    }

    private void Page_MouseUp(PageView page, MouseButtonEventArgs e)
    {
        if (_drawing == null) return;
        page.Surface.ReleaseMouseCapture();
        var view = _drawing;
        _drawing = null;
        if (view.Model.Width < 6 && view.Model.Height < 6)
        {
            view.Model.Width = 100;
            view.Model.Height = 24;
            view.Model.CenterX = _drawStart.X;
            view.Model.CenterY = _drawStart.Y;
        }
        CommitGesture();
        SetTool(Tool.Select);
        Select(view);
        e.Handled = true;
    }

    private StampModel NewText(int page, string text, double size, Color color)
    {
        var m = new StampModel
        {
            Kind = StampKind.Text, PageIndex = page, Text = text, FontSize = size, Color = color,
            FontFamily = _textFont, Bold = _textBold, Italic = _textItalic,
        };
        var s = StampView.MeasureText(m);
        m.Width = s.Width;
        m.Height = s.Height;
        return m;
    }

    /// <summary>Positions a text stamp so the first line starts at the click point.</summary>
    private static void PlaceTextAt(StampModel m, Point p)
    {
        double lineHeight = m.FontSize * PdfSaver.LineSpacing;
        m.CenterX = p.X - 2 + m.Width / 2;
        m.CenterY = p.Y - lineHeight / 2 - PdfSaver.TextPadding + m.Height / 2;
    }

    private void BeginPlaceImage(byte[] image, bool signature)
    {
        _pendingIsSignature = signature;
        SetTool(Tool.PlaceImage);
        _pendingImage = image;
        SelectTool.IsChecked = false;
    }

    private void OnSignatureFieldClicked(WidgetModel widget, Rect r)
    {
        var dlg = new SignatureDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedPng == null) return;
        var img = ImageUtil.FromBytes(dlg.SelectedPng);
        double aspect = (double)img.PixelWidth / img.PixelHeight;
        bool vertical = r.Height > r.Width * 1.3;
        double length = vertical ? r.Height : r.Width, thickness = vertical ? r.Width : r.Height;
        double w = Math.Min(length * 0.95, thickness * 1.1 * aspect);
        var m = new StampModel { Kind = StampKind.Image, PageIndex = widget.PageIndex, ImageData = dlg.SelectedPng };
        m.Width = w; m.Height = w / aspect;
        m.CenterX = r.X + r.Width / 2; m.CenterY = r.Y + r.Height / 2;
        m.Angle = vertical ? 270 : 0;
        Mutate(() => AddStamp(m));
        SetStatus("Signature placed. Drag to adjust; use ⟲/⟳ or the top handle to rotate.");
    }

    // =====================================================================================
    // UI helpers
    // =====================================================================================

    private void UpdateUi()
    {
        bool hasDoc = _bytes != null;
        EmptyState.Visibility = hasDoc ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = SaveMenu.IsEnabled = SaveAsMenu.IsEnabled = SaveFlatMenu.IsEnabled = hasDoc;
        PrintButton.IsEnabled = PrintMenu.IsEnabled = hasDoc;
        ToolsBar.IsEnabled = hasDoc;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        PageCountText.Text = $"/ {_pages.Count}";
        UpdateTitle();
        UpdateCurrentPage();
    }

    private void UpdateTitle()
    {
        Title = _bytes == null ? "PDF Editor" : $"{(_dirty ? "● " : "")}{Path.GetFileName(_path)} – PDF Editor";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowError(string message, Exception ex)
    {
        Mouse.OverrideCursor = null;
        MessageBox.Show(this, message + "\n\n" + ex.Message, "PDF Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool IsTyping() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
        || (Keyboard.FocusedElement is ComboBox cb && cb.IsEditable);

    // =====================================================================================
    // Event handlers
    // =====================================================================================

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Title = "Open PDF", Filter = "PDF files|*.pdf|All files|*.*" };
        if (ofd.ShowDialog(this) == true) await OpenAsync(ofd.FileName);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveCurrentAsync();
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync(false);
    private async void SaveFlat_Click(object sender, RoutedEventArgs e) => await SaveAsAsync(true);
    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => RotateSelection(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => RotateSelection(90);
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    private void CopyToAll_Click(object sender, RoutedEventArgs e) => CopySelectionToAllPages();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);
    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth();
    private void ActualSize_Click(object sender, RoutedEventArgs e) { _fitWidth = false; SetZoom(1); }

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        FieldOverlay.Highlight = HighlightMenu.IsChecked;
        BuildFieldOverlays();
    }

    private void Signature_Click(object sender, RoutedEventArgs e)
    {
        if (_bytes == null) return;
        var dlg = new SignatureDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedPng != null) BeginPlaceImage(dlg.SelectedPng, signature: true);
    }

    private void Image_Click(object sender, RoutedEventArgs e)
    {
        if (_bytes == null) return;
        var ofd = new OpenFileDialog { Title = "Choose an image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (ofd.ShowDialog(this) != true) return;
        try
        {
            BeginPlaceImage(ImageUtil.PrepareForPlacement(ofd.FileName), signature: false);
        }
        catch (Exception ex)
        {
            ShowError("Could not load that image.", ex);
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Filling forms\n" +
            "  • Fillable fields are highlighted in blue – click and type. Tab moves between fields.\n" +
            "  • For forms without fields, use Text, Date, ✓ and ✗ and click where they should go.\n\n" +
            "Signing\n" +
            "  • Click ✍ Sign, draw or import your signature once (it is remembered), then click on the page.\n" +
            "  • Drag to move, the corner handle resizes, the round top handle rotates (Shift = 15° steps).\n" +
            "  • ⟲ / ⟳ rotate by 90° – handy for signature lines printed vertically. You can also type an exact angle.\n" +
            "  • Yellow signature fields: click them to drop your signature right in the box.\n\n" +
            "Other\n" +
            "  • Double-click placed text to edit it. Choose its font, size and colour in the toolbar; Ctrl+B / Ctrl+I for bold and italic.\n" +
            "  • Del deletes, arrows nudge (Shift = 10×).\n" +
            "  • White-out hides existing content under a white box. It is NOT redaction: the covered text\n" +
            "    is still in the file and can be selected, searched and copied. Don't use it for confidential data.\n" +
            "  • Ctrl+Z / Ctrl+Y undo/redo form entries and placed items (inside a field, Ctrl+Z undoes typing there).\n" +
            "  • Ctrl+mouse wheel zooms.\n" +
            "  • Items you place stay movable and editable when you reopen the saved file here.\n" +
            "  • File › Save Flattened Copy makes form entries and placed items permanent (no longer editable).\n" +
            "  • Ctrl+P prints the document with everything you filled in, exactly as a flattened copy would look.",
            "How to use PDF Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem item)
            Dispatcher.BeginInvoke(() => ApplyZoomText(item.Content?.ToString() ?? ""));
    }

    private void ZoomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyZoomText(ZoomBox.Text); Scroller.Focus(); e.Handled = true; }
    }

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(PageBox.Text, out var n)) GoToPage(n - 1);
        Scroller.Focus();
        UpdateCurrentPage();
        e.Handled = true;
    }

    private void ApplyAngleBox()
    {
        if (_selected == null || _updatingSelectionBar) return;
        if (double.TryParse(AngleBox.Text.Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.CurrentCulture, out var a))
        {
            var m = _selected.Model;
            Mutate(() => m.Angle = a);
        }
        UpdateSelectionBar();
    }

    private void AngleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyAngleBox(); e.Handled = true; }
    }

    private void AngleBox_LostFocus(object sender, RoutedEventArgs e) => ApplyAngleBox();

    private void ApplyFontSize(string text)
    {
        if (_updatingSelectionBar) return;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) || size < 4 || size > 144) return;
        _textFontSize = size;
        if (_selected?.Model is { Kind: StampKind.Text } m && Math.Abs(m.FontSize - size) > 0.01)
            Mutate(() => m.FontSize = size);
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontSizeBox.SelectedItem is string s) ApplyFontSize(s);
    }

    private void FontSizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyFontSize(FontSizeBox.Text); e.Handled = true; }
    }

    /// <summary>Fonts listed first, when installed; the rest of the installed fonts follow alphabetically.</summary>
    private static readonly string[] CommonFonts =
        { "Arial", "Calibri", "Cambria", "Georgia", "Segoe UI", "Times New Roman", "Verdana", "Courier New", "Segoe Script", "Segoe Print" };

    private void FillFontBox()
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source)
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("@"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var common = CommonFonts.Where(c => installed.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var name in common) FontBox.Items.Add(FontItem(name));
        if (common.Count > 0) FontBox.Items.Add(new Separator());
        foreach (var name in installed) FontBox.Items.Add(FontItem(name));
    }

    /// <summary>A font list entry showing the name in its own typeface (TextSearch lets typing jump by name).</summary>
    private static ComboBoxItem FontItem(string name)
    {
        var item = new ComboBoxItem { Tag = name, Content = new TextBlock { Text = name, FontFamily = new FontFamily(name), FontSize = 13 } };
        TextSearch.SetText(item, name);
        return item;
    }

    private void SelectFont(string name)
    {
        var item = FontBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, name, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            // A font that isn't installed here (e.g. text placed on another PC): list it so it can still be shown.
            item = FontItem(name);
            FontBox.Items.Insert(0, item);
        }
        FontBox.SelectedItem = item;
    }

    private void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelectionBar || FontBox.SelectedItem is not ComboBoxItem { Tag: string name }) return;
        _textFont = name;
        SaveTextPreferences();
        if (_selected?.Model is { Kind: StampKind.Text } m && m.FontFamily != name)
            Mutate(() => m.FontFamily = name);
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => SetBold(BoldButton.IsChecked == true);
    private void Italic_Click(object sender, RoutedEventArgs e) => SetItalic(ItalicButton.IsChecked == true);

    private void SetBold(bool bold)
    {
        _textBold = bold;
        SaveTextPreferences();
        if (_selected?.Model is { Kind: StampKind.Text } m && m.Bold != bold) Mutate(() => m.Bold = bold);
        UpdateSelectionBar();
    }

    private void SetItalic(bool italic)
    {
        _textItalic = italic;
        SaveTextPreferences();
        if (_selected?.Model is { Kind: StampKind.Text } m && m.Italic != italic) Mutate(() => m.Italic = italic);
        UpdateSelectionBar();
    }

    private void SaveTextPreferences() =>
        (UserSettings.Load() with { TextFont = _textFont, TextBold = _textBold, TextItalic = _textItalic }).Save();

    private void ColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelectionBar || ColorBox.SelectedItem is not ComboBoxItem { Tag: Color c }) return;
        _textColor = c;
        if (_selected?.Model is { Kind: StampKind.Text } m && m.Color != c)
            Mutate(() => m.Color = c);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: Open_Click(this, e); e.Handled = true; return;
                case Key.S when shift: SaveAs_Click(this, e); e.Handled = true; return;
                case Key.S: Save_Click(this, e); e.Handled = true; return;
                case Key.P: Print_Click(this, e); e.Handled = true; return;
                case Key.OemPlus or Key.Add: StepZoom(1); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: StepZoom(-1); e.Handled = true; return;
                case Key.D0 or Key.NumPad0: FitWidth(); e.Handled = true; return;
                case Key.D1 or Key.NumPad1: _fitWidth = false; SetZoom(1); e.Handled = true; return;
            }
            // Ctrl+B / Ctrl+I: the selected text (even while editing it), or the style for new text.
            bool textTarget = _selected?.Model.Kind == StampKind.Text ? (_selected.IsEditing || !IsTyping()) : (_selected == null && !IsTyping());
            if (textTarget && key is Key.B or Key.I)
            {
                bool current = key == Key.B ? (_selected?.Model.Bold ?? _textBold) : (_selected?.Model.Italic ?? _textItalic);
                if (key == Key.B) SetBold(!current); else SetItalic(!current);
                e.Handled = true;
                return;
            }
            if (!IsTyping())
            {
                switch (key)
                {
                    case Key.Z: Undo(); e.Handled = true; return;
                    case Key.Y: Redo(); e.Handled = true; return;
                    case Key.R: RotateSelection(shift ? -90 : 90); e.Handled = true; return;
                }
            }
        }

        if (key == Key.Escape)
        {
            if (_tool != Tool.Select) { SetTool(Tool.Select); e.Handled = true; }
            else if (_selected != null && !_selected.IsEditing) { Select(null); e.Handled = true; }
            return;
        }

        if (IsTyping() || _selected == null) return;

        switch (key)
        {
            case Key.Delete or Key.Back:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
            {
                double step = shift ? 10 : 1;
                var m = _selected.Model;
                Mutate(() =>
                {
                    if (key == Key.Left) m.CenterX -= step;
                    if (key == Key.Right) m.CenterX += step;
                    if (key == Key.Up) m.CenterY -= step;
                    if (key == Key.Down) m.CenterY += step;
                });
                e.Handled = true;
                break;
            }
            case Key.Enter when _selected.Model.Kind == StampKind.Text:
                _selected.BeginEdit();
                e.Handled = true;
                break;
        }
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        var file = files[0];
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".pdf")
        {
            await OpenAsync(file);
            return;
        }
        if (ImageExtensions.Contains(ext) && _bytes != null)
        {
            var page = _pages.FirstOrDefault(p => p.Surface.InputHitTest(e.GetPosition(p.Surface)) != null);
            if (page == null) { SetStatus("Drop the image onto a page to place it."); return; }
            try
            {
                var data = ImageUtil.PrepareForPlacement(file);
                var img = ImageUtil.FromBytes(data);
                var p = e.GetPosition(page.Surface);
                double w = Math.Min(220, img.PixelWidth * 0.75);
                var m = new StampModel { Kind = StampKind.Image, PageIndex = page.Index, ImageData = data };
                m.Width = w; m.Height = w * img.PixelHeight / img.PixelWidth; m.CenterX = p.X; m.CenterY = p.Y;
                Mutate(() => AddStamp(m));
            }
            catch (Exception ex)
            {
                ShowError("Could not load that image.", ex);
            }
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CommitTextEdits();
        if (!ConfirmDiscardChanges()) e.Cancel = true;
    }
}
