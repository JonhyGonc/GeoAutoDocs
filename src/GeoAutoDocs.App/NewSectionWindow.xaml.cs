using System.Globalization;
using System.Windows;

namespace GeoAutoDocs.App;

public partial class NewSectionWindow : Window
{
    private NewSectionWindow()
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 740, 620);
        Loaded += (_, _) => NameBox.Focus();
    }

    public NewSectionDraft? Draft { get; private set; }

    public static NewSectionDraft? Show(Window owner)
    {
        var window = new NewSectionWindow { Owner = owner };
        return window.ShowDialog() == true ? window.Draft : null;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
        decimal? diameter = null;
        if (!string.IsNullOrWhiteSpace(DiameterBox.Text))
        {
            if (!decimal.TryParse(DiameterBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed))
            {
                DiameterBox.Focus(); DiameterBox.SelectAll(); return;
            }
            diameter = parsed;
        }
        Draft = new NewSectionDraft(NameBox.Text.Trim(), Empty(DescriptionBox.Text), diameter,
            Empty(StartPointBox.Text), Empty(EndPointBox.Text), Empty(ReferenceBox.Text));
        DialogResult = true;
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record NewSectionDraft(string Name, string? Description, decimal? Diameter,
    string? StartPoint, string? EndPoint, string? Reference);
