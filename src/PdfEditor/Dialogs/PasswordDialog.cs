using System.Windows;
using System.Windows.Controls;

namespace PdfEditor.Dialogs;

/// <summary>Minimal prompt for a document password.</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _box = new() { Margin = new Thickness(0, 8, 0, 14), Padding = new Thickness(4) };

    public PasswordDialog(string message)
    {
        Title = "Password required";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("ChromeBrush");

        var ok = new Button { Content = "Open", IsDefault = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        ok.Click += (s, e) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (s, e) => _box.Focus();
    }

    public string Password => _box.Password;
}
