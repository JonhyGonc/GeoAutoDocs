using System.Windows;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class NewProjectWindow : Window
{
    private NewProjectWindow()
    {
        InitializeComponent();
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
        if (dialog.ShowDialog(this) == true) LogoBox.Text = dialog.FileName;
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
}

public sealed record NewProjectDraft(string Name, string Client, string Cnpj, string Area,
    string Contract, string PurchaseOrder, string? LogoPath);
