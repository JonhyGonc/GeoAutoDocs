using System.Globalization;
using System.Windows;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.App;

public partial class CorrectionReviewWindow : Window
{
    public CorrectionReviewWindow(IReadOnlyList<InspectionCorrectionResolution> corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 860, 680);

        var items = corrections.Select(x => new CorrectionReviewDisplayItem(
            x.Status.ToString(),
            StatusText(x.Status),
            SectionLabel(x),
            x.Correction.HasMeterage,
            FormatMeterage(x.CurrentMeterage),
            FormatMeterage(x.ProposedMeterage),
            x.Correction.HasDescription,
            DisplayDescription(x.CurrentDescription),
            DisplayDescription(x.ProposedDescription),
            x.Message)).ToList();

        CorrectionsList.ItemsSource = items;
        var applicable = corrections.Count(x => x.CanApply);
        var invalid = corrections.Count(x =>
            x.Status is InspectionCorrectionResolutionStatus.Invalid or
                InspectionCorrectionResolutionStatus.NotFound);
        var unchanged = corrections.Count(x =>
            x.Status == InspectionCorrectionResolutionStatus.Unchanged);

        SummaryText.Text = $"{corrections.Count} correção(ões) recebida(s) • " +
                           $"{applicable} pronta(s) para aplicar";
        if (invalid > 0 || unchanged > 0)
        {
            WarningText.Text =
                $"{invalid} item(ns) não pode(m) ser localizado(s) ou validado(s); " +
                $"{unchanged} já estava(m) atualizado(s). Somente itens válidos serão aplicados.";
            WarningText.Visibility = Visibility.Visible;
        }

        ApplyButton.IsEnabled = applicable > 0;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string SectionLabel(InspectionCorrectionResolution item)
    {
        var section = string.IsNullOrWhiteSpace(item.SectionName)
            ? $"Trecho {item.Correction.SectionId:D}"
            : item.SectionName;
        return string.IsNullOrWhiteSpace(item.LocationName)
            ? section
            : $"{item.LocationName} — {section}";
    }

    private static string StatusText(InspectionCorrectionResolutionStatus status) => status switch
    {
        InspectionCorrectionResolutionStatus.Ready => "Pronta",
        InspectionCorrectionResolutionStatus.Unchanged => "Sem alteração",
        InspectionCorrectionResolutionStatus.NotFound => "Não encontrada",
        _ => "Inválida"
    };

    private static string FormatMeterage(decimal? value) => value is null
        ? "Não informada"
        : $"{value.Value.ToString("0.##", CultureInfo.CurrentCulture)} m";

    private static string DisplayDescription(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(sem texto)" : value;

    private sealed record CorrectionReviewDisplayItem(
        string Status,
        string StatusText,
        string SectionLabel,
        bool HasMeterage,
        string CurrentMeterage,
        string ProposedMeterage,
        bool HasDescription,
        string CurrentDescription,
        string ProposedDescription,
        string Message);
}
