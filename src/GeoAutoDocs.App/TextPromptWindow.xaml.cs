using System.Windows;

namespace GeoAutoDocs.App;

public partial class TextPromptWindow : Window
{
    private TextPromptWindow(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 700, 360);
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string initialValue = "")
    {
        var window = new TextPromptWindow(title, prompt, initialValue) { Owner = owner };
        return window.ShowDialog() == true ? window.ValueBox.Text.Trim() : null;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text)) return;
        DialogResult = true;
    }
}
