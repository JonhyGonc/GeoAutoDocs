using System.Windows;

namespace GeoAutoDocs.App;

public partial class NewLocationWindow : Window
{
    private NewLocationWindow()
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 560, 360);
        Loaded += (_, _) => NameBox.Focus();
    }

    public string? LocationName { get; private set; }

    public static string? Show(Window owner)
    {
        var window = new NewLocationWindow { Owner = owner };
        return window.ShowDialog() == true ? window.LocationName : null;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            NameBox.Focus();
            return;
        }

        LocationName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
