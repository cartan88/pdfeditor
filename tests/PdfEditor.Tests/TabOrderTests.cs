using System.Windows;
using System.Windows.Media;
using PdfEditor.Controls;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using PdfSharp.Pdf;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>Tab order follows the page as read, not the order fields are stored in the PDF.</summary>
public class TabOrderTests
{
    /// <summary>Page 1: rows; page 2: /Tabs /C (columns); page 3: /Rotate 90. Fields are stored deliberately scrambled.</summary>
    private static byte[] ScrambledForm() => Form(new FieldSpec[]
    {
        new("e3", 300, 100, 20, 60, Page: 2), new("c1", 300, 500, 150, 20), new("d_right_top", 300, 700, 100, 20, Page: 1),
        new("a3", 400, 698, 120, 24), new("b3", 300, 580, 150, 20), new("e2", 100, 400, 20, 60, Page: 2),
        new("a1", 100, 700, 150, 20), new("c2", 50, 500, 150, 20), new("d_left_bottom", 100, 600, 100, 20, Page: 1),
        new("b2", 300, 615, 150, 20), new("a2", 300, 702, 16, 16), new("e1", 100, 100, 20, 60, Page: 2),
        new("b1", 100, 560, 150, 80), new("d_left_top", 100, 700, 100, 20, Page: 1),
    }, pages: 3, configure: doc =>
    {
        doc.Pages[1].Elements["/Tabs"] = new PdfName("/C");
        doc.Pages[2].Elements["/Rotate"] = new PdfInteger(90);
    });

    private static readonly string[] ReadingOrder =
        { "a1", "a2", "a3", "b1", "b2", "b3", "c2", "c1", "d_left_top", "d_left_bottom", "d_right_top", "e1", "e2", "e3" };

    [Fact]
    public void SortsFieldsIntoReadingOrder()
    {
        var pdf = PdfOpen.Open(ScrambledForm(), null);
        var geometry = PdfOpen.ReadGeometry(pdf);
        var models = FormReader.Read(pdf).Select(b => b.Model).ToList();
        Assert.Equal("e3", models[0].FullName); // stored order really is scrambled
        Assert.True(geometry[1].ColumnTabOrder);
        Assert.False(geometry[0].ColumnTabOrder);

        var byPage = models.SelectMany(m => m.Widgets).GroupBy(w => w.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        var order = geometry.SelectMany(g => FieldOverlay.TabOrder(byPage[g.Index], g)).Select(x => x.Widget.Field.FullName);
        Assert.Equal(ReadingOrder, order);
    }

    [Fact]
    public Task ControlsAreAddedInReadingOrder() => Ui.Run(() =>
    {
        // WPF tabs through controls with equal TabIndex in the order they were added to the page.
        var pdf = PdfOpen.Open(ScrambledForm(), null);
        var geometry = PdfOpen.ReadGeometry(pdf);
        var widgets = FormReader.Read(pdf).SelectMany(b => b.Model.Widgets).ToList();
        var names = geometry.SelectMany(g =>
        {
            var page = new PageView(g, new ScaleTransform(1, 1));
            FieldOverlay.Build(page, widgets.Where(w => w.PageIndex == g.Index), (w, r) => { });
            return page.FieldLayer.Children.OfType<FrameworkElement>().Select(c => (string)c.ToolTip);
        });
        Assert.Equal(ReadingOrder, names);
    });

    [Fact]
    public void SampleFormTabsTopToBottom()
    {
        var pdf = PdfOpen.Open(SampleForm(), null);
        var geometry = PdfOpen.ReadGeometry(pdf);
        var widgets = FormReader.Read(pdf).SelectMany(b => b.Model.Widgets).Where(w => w.PageIndex == 0);
        var order = FieldOverlay.TabOrder(widgets, geometry[0]).Select(x => x.Widget.Field.FullName).Distinct();
        Assert.Equal(new[] { "full_name", "dob", "address", "country", "subscribe", "plan" }, order);
    }
}
