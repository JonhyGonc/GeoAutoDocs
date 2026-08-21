using System.Globalization;
using System.Windows;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.App;

public partial class NewSectionWindow : Window
{
    private NewSectionWindow(InspectionSection? section)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 740, 650);
        if (section is not null)
        {
            Title = "Editar trecho";
            FormTitleText.Text = "Editar trecho";
            SaveButton.Content = "Salvar alterações";
            NameBox.Text = section.Name;
            StartPointBox.Text = section.StartPoint ?? "";
            EndPointBox.Text = section.EndPoint ?? "";
            DiameterBox.Text = MeasurementText.Format(section.Diameter);
            InvestigatedMetersBox.Text = MeasurementText.Format(section.InvestigatedMeters);
            ReferenceBox.Text = section.Reference ?? "";
            DescriptionBox.Text = section.Description ?? "";
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    public NewSectionDraft? Draft { get; private set; }

    public static NewSectionDraft? Show(Window owner, InspectionSection? section = null)
    {
        var window = new NewSectionWindow(section) { Owner = owner };
        return window.ShowDialog() == true ? window.Draft : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
        if (!TryOptionalMeasurement(DiameterBox, out var diameter) ||
            !TryOptionalMeasurement(InvestigatedMetersBox, out var investigatedMeters)) return;
        Draft = new NewSectionDraft(NameBox.Text.Trim(), Empty(DescriptionBox.Text), diameter,
            Empty(StartPointBox.Text), Empty(EndPointBox.Text), Empty(ReferenceBox.Text), investigatedMeters);
        DialogResult = true;
    }

    private static bool TryOptionalMeasurement(System.Windows.Controls.TextBox box, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(box.Text)) return true;
        if (MeasurementText.TryParse(box.Text, out var parsed)) { value = parsed; return true; }
        box.Focus();
        box.SelectAll();
        return false;
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record NewSectionDraft(string Name, string? Description, decimal? Diameter,
    string? StartPoint, string? EndPoint, string? Reference, decimal? InvestigatedMeters);
