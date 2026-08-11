using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class NewProjectWindow : Window
{
    private NewProjectWindow()
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 1120, 780);
        Loaded += (_, _) => NameBox.Focus();
    }

    public NewProjectDraft? Draft { get; private set; }

    public static NewProjectDraft? Show(Window owner)
    {
        var window = new NewProjectWindow { Owner = owner };
        return window.ShowDialog() == true ? window.Draft : null;
    }

    private void SelectLogo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Selecione o logo do cliente"
        };
        if (dialog.ShowDialog(this) == true)
        {
            LogoBox.Text = dialog.FileName;
            LogoPreview.Source = LoadPreview(dialog.FileName);
            LogoStatus.Text = "Selecionada";
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Focus();
            return;
        }

        Draft = new NewProjectDraft(
            NameBox.Text.Trim(), ClientBox.Text.Trim(), CnpjBox.Text.Trim(), AreaBox.Text.Trim(),
            ContractBox.Text.Trim(), PurchaseOrderBox.Text.Trim(),
            string.IsNullOrWhiteSpace(LogoBox.Text) ? null : LogoBox.Text);
        DialogResult = true;
    }

    private static BitmapImage? LoadPreview(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 180;
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}

public sealed record NewProjectDraft(string Name, string Client, string Cnpj, string Area,
    string Contract, string PurchaseOrder, string? LogoPath);
