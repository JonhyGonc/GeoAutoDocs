using System.Windows;
using GeoAutoDocs.Infrastructure;

namespace GeoAutoDocs.App;

public partial class ReportValidationWindow : Window
{
    public ReportValidationWindow(ReportValidationResult result)
    {
        InitializeComponent();
        SummaryText.Text = $"✓ {result.SelectedSections} trecho(s) selecionado(s)\n" +
                           $"✓ {result.Occurrences} anomalia(s)\n" +
                           $"✓ {result.Evidences} evidência(s)";
        ErrorsPanel.Visibility = result.Errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WarningsPanel.Visibility = result.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ErrorsText.Text = string.Join("\n", result.Errors.Select(x => "• " + x));
        WarningsText.Text = string.Join("\n", result.Warnings.Select(x => "⚠ " + x));
        GenerateButton.IsEnabled = result.CanGenerate;
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
