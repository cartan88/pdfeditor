using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfEditor.Controls;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Infrastructure;
using static PdfEditor.Tests.Infrastructure.TestData;

namespace PdfEditor.Tests;

/// <summary>
/// The real main window, driven without being shown (no window ever appears on screen). Documents are always temp
/// copies, and every test marks the document saved before finishing so no "Save changes?" prompt can appear.
/// </summary>
public class MainWindowTests
{
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

    private sealed class Driver(MainWindow win)
    {
        public MainWindow Window { get; } = win;
        private object? Get(string field) => typeof(MainWindow).GetField(field, F)!.GetValue(Window);
        private object? Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method, F)!.Invoke(Window, args);

        public List<FormFieldModel> Fields => (List<FormFieldModel>)Get("_fields")!;
        public FormFieldModel Field(string name) => Fields.First(f => f.FullName == name);
        public List<PageView> Pages => (List<PageView>)Get("_pages")!;
        public List<StampModel> Stamps => ((System.Collections.IList)Get("_stamps")!).Cast<object>()
            .Select(v => (StampModel)v.GetType().GetProperty("Model")!.GetValue(v)!).ToList();
        public int UndoCount => ((System.Collections.ICollection)Get("_undo")!).Count;
        public bool Dirty => (bool)Get("_dirty")!;
        public bool CanUndo => ((Button)Window.FindName("UndoButton")).IsEnabled;
        public bool CanRedo => ((Button)Window.FindName("RedoButton")).IsEnabled;

        public void Undo() => Call("Undo");
        public void Redo() => Call("Redo");
        public void AddItem(StampModel m) => Call("Mutate", (Action)(() => Call("AddStamp", m, false)));
        public Task<bool> Save(string path) => (Task<bool>)Call("SaveAsync", path, false)!;
        public void MarkSaved() => typeof(MainWindow).GetField("_dirty", F)!.SetValue(Window, false);

        public T Control<T>(string name, int nth = 0) where T : FrameworkElement =>
            Pages.SelectMany(p => p.FieldLayer.Children.OfType<T>()).Where(c => (string)c.ToolTip == name).ElementAt(nth);

        /// <summary>Types into a field's TextBox one character at a time, as keystrokes would.</summary>
        public void Type(string field, string text)
        {
            var box = Control<TextBox>(field);
            foreach (var c in text) box.Text += c;
        }

        /// <summary>Focus leaving a field ends its typing session (one undo step per session).</summary>
        public void Leave(string field) => Control<TextBox>(field).RaiseEvent(
            new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, Control<TextBox>(field), null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });

        public void Click(FrameworkElement el) =>
            el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
    }

    private static async Task<Driver> Open(string path)
    {
        var win = new MainWindow();
        await win.OpenAsync(path);
        return new Driver(win);
    }

    [Fact]
    public Task UndoCoversFieldEditsAndPlacedItems() => Ui.Run(async () =>
    {
        var d = await Open(TempCopyOfSample());
        try
        {
            var name = d.Field("full_name");
            var dob = d.Field("dob");
            var subscribe = d.Field("subscribe");
            Assert.False(d.CanUndo);

            d.Type("full_name", "Jane Doe");
            Assert.Equal(1, d.UndoCount); // eight keystrokes, one step
            Assert.True(d.Dirty);
            d.Leave("full_name");
            d.Type("dob", "1990-05-01");
            d.Leave("dob");
            d.Click(d.Control<Border>("subscribe"));
            d.AddItem(Item(StampKind.Text, 0, 300, 60, 60, 16, text: "Approved"));
            Assert.Equal(4, d.UndoCount);

            d.Undo();
            Assert.Empty(d.Stamps);
            Assert.NotEqual("Off", subscribe.Value);
            d.Undo();
            Assert.Equal("Off", subscribe.Value);
            d.Undo();
            Assert.Equal("", dob.Value);
            Assert.Equal("", d.Control<TextBox>("dob").Text);
            d.Undo();
            Assert.Equal("", name.Value);
            Assert.False(name.IsDirty);
            Assert.False(d.CanUndo);
            Assert.True(d.CanRedo);

            d.Redo(); d.Redo(); d.Redo(); d.Redo();
            Assert.Equal("Jane Doe", name.Value);
            Assert.Equal("1990-05-01", dob.Value);
            Assert.NotEqual("Off", subscribe.Value);
            Assert.Single(d.Stamps);
            Assert.False(d.CanRedo);
        }
        finally { d.MarkSaved(); }
    });

    [Fact]
    public Task EachClickAndPickIsItsOwnStep() => Ui.Run(async () =>
    {
        var d = await Open(TempCopyOfSample());
        try
        {
            var plan = d.Field("plan");
            string start = plan.Value;
            d.Click(d.Control<Border>("plan", 1));
            d.Click(d.Control<Border>("plan", 2));
            d.Undo();
            Assert.Equal(plan.Widgets[1].OnState, plan.Value);
            d.Undo();
            Assert.Equal(start, plan.Value);

            var country = d.Field("country");
            string original = country.Value;
            var combo = d.Control<ComboBox>("country");
            combo.SelectedIndex = 1;
            combo.SelectedIndex = 2;
            d.Undo();
            Assert.Equal(country.Options[1].Export, country.Value);
            d.Undo();
            Assert.Equal(original, country.Value);
        }
        finally { d.MarkSaved(); }
    });

    [Fact]
    public Task ANewEditAfterUndoClearsRedo() => Ui.Run(async () =>
    {
        var d = await Open(TempCopyOfSample());
        try
        {
            d.Type("full_name", "Jane");
            d.Leave("full_name");
            d.Undo();
            Assert.True(d.CanRedo);
            d.Type("dob", "X");
            Assert.False(d.CanRedo);
        }
        finally { d.MarkSaved(); }
    });

    [Fact]
    public Task PlacedItemsAreEditableAfterSavingAndReopening() => Ui.Run(async () =>
    {
        var path = TempCopyOfSample();
        var d = await Open(path);
        try
        {
            d.AddItem(Item(StampKind.Text, 0, 300, 120, 60, 16, angle: 30, text: "Approved"));
            d.AddItem(Item(StampKind.Image, 1, 200, 200, 120, 45, data: SignaturePng()));
            Assert.True(await d.Save(path));
        }
        finally { d.MarkSaved(); }

        double x, y, width;
        var reopened = await Open(path);
        try
        {
            Assert.Equal(2, reopened.Stamps.Count);
            Assert.False(reopened.Dirty);
            var approved = reopened.Stamps.First(s => s.Text == "Approved");
            approved.CenterX += 40;
            approved.Text = "Approved (edited)"; // longer text resizes the box around its rotated top-left corner
            (x, y, width) = (approved.CenterX, approved.CenterY, approved.Width);
            Assert.True(await reopened.Save(path));
        }
        finally { reopened.MarkSaved(); }

        var third = await Open(path);
        try
        {
            Assert.Equal(2, third.Stamps.Count); // nothing duplicated
            var edited = third.Stamps.First(s => s.Text == "Approved (edited)");
            Assert.Equal(x, edited.CenterX, 6);
            Assert.Equal(y, edited.CenterY, 6);
            Assert.Equal(width, edited.Width, 6);
        }
        finally { third.MarkSaved(); }
    });
}
