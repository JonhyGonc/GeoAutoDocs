using System.Windows;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class CustomSectionWindow : Window
{
    private readonly List<string> _imagePaths = [];
    public string SectionTitle => TitleBox.Text.Trim();
    public string SectionText => TextBox.Text.Trim();
    public IReadOnlyList<string> ImagePaths => _imagePaths;

    public CustomSectionWindow()
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 740, 620);
    }

    private void AddImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames.Where(x => !_imagePaths.Contains(x, StringComparer.OrdinalIgnoreCase)))
            _imagePaths.Add(file);
        ImagesList.ItemsSource = null;
        ImagesList.ItemsSource = _imagePaths;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SectionTitle) && string.IsNullOrWhiteSpace(SectionText) && _imagePaths.Count == 0)
        {
            MessageBox.Show("Informe um título, texto ou imagem.", "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
