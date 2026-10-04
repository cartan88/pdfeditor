using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfEditor.Controls;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>The live WPF controls laid over each page's form fields.</summary>
public class FieldOverlayTests
{
    private sealed record Built(List<FormFieldModel> Fields, List<PageView> Pages)
    {
        public FormFieldModel Field(string name) => Fields.First(f => f.FullName == name);
        public T Control<T>(string name, int nth = 0) where T : FrameworkElement =>
            Pages.SelectMany(p => p.FieldLayer.Children.OfType<T>()).Where(c => (string)c.ToolTip == name).ElementAt(nth);
        public void Rebuild()
        {
            var byPage = Fields.SelectMany(f => f.Widgets).GroupBy(w => w.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var p in Pages) FieldOverlay.Build(p, byPage.TryGetValue(p.Index, out var l) ? l : new List<WidgetModel>(), (w, r) => { });
        }
    }

    private static Built Build(byte[] pdfBytes)
    {
        var pdf = PdfOpen.Open(pdfBytes, null);
        var built = new Built(FormReader.Read(pdf).Select(b => b.Model).ToList(),
            PdfOpen.ReadGeometry(pdf).Select(g => new PageView(g, new ScaleTransform(1, 1))).ToList());
        built.Rebuild();
        return built;
    }

    private static int Handlers(FormFieldModel f) =>
        (typeof(FormFieldModel).GetField("PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f) as Delegate)?.GetInvocationList().Length ?? 0;

    private static TextBlock Glyph(Border toggle) => (TextBlock)((Viewbox)toggle.Child).Child;

    [Fact]
    public Task RebuildingDoesNotLeakControlsOrHandlers() => Ui.Run(() =>
    {
        var b = Build(SampleForm());
        var watched = new[] { b.Field("subscribe"), b.Field("plan"), b.Field("country") };
        var counts = watched.Select(Handlers).ToArray();
        var first = b.Pages.SelectMany(p => p.FieldLayer.Children.Cast<UIElement>()).Select(c => new WeakReference(c)).ToList();

        for (int i = 0; i < 50; i++)
        {
            FieldOverlay.Highlight = i % 2 == 0;
            b.Rebuild();
        }
        FieldOverlay.Highlight = true;
        b.Rebuild();

        Assert.Equal(counts, watched.Select(Handlers).ToArray());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(0, first.Count(w => w.IsAlive));
    });

    [Fact]
    public Task ControlsAndFieldsStayInSync() => Ui.Run(() =>
    {
        var b = Build(SampleForm());
        var subscribe = b.Field("subscribe");
        subscribe.Value = subscribe.Widgets[0].OnState;
        Assert.Equal(Visibility.Visible, Glyph(b.Control<Border>("subscribe")).Visibility);
        subscribe.Value = "Off";
        Assert.Equal(Visibility.Hidden, Glyph(b.Control<Border>("subscribe")).Visibility);

        var plan = b.Field("plan");
        plan.Value = plan.Widgets[1].OnState;
        var shown = Enumerable.Range(0, 3).Select(i => Glyph(b.Control<Border>("plan", i)).Visibility == Visibility.Visible).ToArray();
        Assert.Equal(new[] { false, true, false }, shown);

        var country = b.Field("country");
        var combo = b.Control<ComboBox>("country");
        country.Value = country.Options[2].Export;
        Assert.Equal(2, combo.SelectedIndex);
        combo.SelectedIndex = 1;
        Assert.Equal(country.Options[1].Export, country.Value);

        var name = b.Field("full_name");
        name.Value = "hello";
        Assert.Equal("hello", b.Control<TextBox>("full_name").Text);
        b.Control<TextBox>("full_name").Text = "typed";
        Assert.Equal("typed", name.Value);
    });

    private static byte[] SpecialFields() => Form(new FieldSpec[]
    {
        new("password", 100, 700, 200, 20, "/Helv 10 Tf 0 g", Ff: 1 << 13),
        new("comb", 100, 650, 120, 20, "/Helv 12 Tf 0 g", Ff: 1 << 24, MaxLen: 6),
        new("auto", 100, 600, 150, 20),
        new("red", 100, 550, 200, 20, "/Helv 11 Tf 1 0 0 rg"),
    });

    [Fact]
    public Task PasswordFieldsAreMaskedAndSynced() => Ui.Run(() =>
    {
        var b = Build(SpecialFields());
        var field = b.Field("password");
        field.Value = "secret";
        var box = b.Pages[0].FieldLayer.Children.OfType<PasswordBox>().Single();
        Assert.Equal('*', box.PasswordChar);
        Assert.Equal("secret", box.Password);
        box.Password = "changed";
        Assert.Equal("changed", field.Value);
        field.Value = "undone";
        Assert.Equal("undone", box.Password);
    });

    [Fact]
    public Task CombFieldsTakeOneCharacterPerCell() => Ui.Run(() =>
    {
        var b = Build(SpecialFields());
        var field = b.Field("comb");
        var input = b.Pages[0].FieldLayer.Children.OfType<Grid>().Single().Children.OfType<TextBox>().Single();
        Assert.Equal(6, input.MaxLength);
        input.Text = "9876";
        Assert.Equal("9876", field.Value);
        field.Value = "12";
        Assert.Equal("12", input.Text);
    });

    [Fact]
    public Task AutoSizedFieldsShrinkAsTextGrowsAndUseTheFieldColour() => Ui.Run(() =>
    {
        var b = Build(SpecialFields());
        var auto = b.Field("auto");
        var box = b.Control<TextBox>("auto");
        auto.Value = "Hi";
        double big = box.FontSize;
        auto.Value = "A much longer value that really cannot fit in here";
        Assert.True(box.FontSize < big);
        Assert.Equal(FieldText.SingleLineSize(auto, auto.Value, 150, 20), box.FontSize);

        Assert.Equal(Color.FromRgb(255, 0, 0), ((SolidColorBrush)b.Control<TextBox>("red").Foreground).Color);
    });
}
