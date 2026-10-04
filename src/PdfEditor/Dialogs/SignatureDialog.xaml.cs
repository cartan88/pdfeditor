using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfEditor.Services;

namespace PdfEditor.Dialogs;

public partial class SignatureDialog : Window
{
    public sealed record Entry(string Path, BitmapSource Image);

    public SignatureDialog()
    {
        InitializeComponent();
        Reload(null);
    }

    /// <summary>PNG bytes of the chosen signature.</summary>
    public byte[]? SelectedPng { get; private set; }

    private void Reload(string? select)
    {
        var entries = new List<Entry>();
        foreach (var path in SignatureLibrary.List())
        {
            try { entries.Add(new Entry(path, ImageUtil.FromBytes(SignatureLibrary.Load(path)))); }
            catch { /* skip files this user can't decrypt or that are damaged */ }
        }
        List.ItemsSource = entries;
        EmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.SelectedItem = entries.FirstOrDefault(e => e.Path == select) ?? entries.FirstOrDefault();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        UseButton.IsEnabled = DeleteButton.IsEnabled = List.SelectedItem != null;
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DrawSignatureDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Png != null)
            Reload(SignatureLibrary.Add(dlg.Png));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Import signature image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (ofd.ShowDialog(this) != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var img = ImageUtil.PrepareSignature(ImageUtil.Load(ofd.FileName), RemoveBackground.IsChecked == true);
            Reload(SignatureLibrary.Add(ImageUtil.ToPng(img)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not load that image:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Entry entry) return;
        if (MessageBox.Show(this, "Delete this saved signature?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SignatureLibrary.Delete(entry.Path);
        Reload(null);
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Entry entry) return;
        SelectedPng = SignatureLibrary.Load(entry.Path);
        DialogResult = true;
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null) Use_Click(sender, e);
    }
}
