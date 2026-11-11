using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.App;

public partial class SectionAutomaticNamingWindow : Window
{
    private readonly Project _project;
    private readonly ObservableCollection<SectionRenamePlanItem> _preview = [];

    public SectionAutomaticNamingWindow(Project project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        InitializeComponent();
        PreviewList.ItemsSource = _preview;
        PrefixBox.Text = SectionAutomaticNaming.DefaultPrefix;
        StartingNumberBox.Text = SectionAutomaticNaming.DefaultStartingNumber
            .ToString(CultureInfo.InvariantCulture);
        RefreshPreview();
    }

    public IReadOnlyList<SectionRenamePlanItem>? RenamePlan { get; private set; }

    private void NamingOption_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || PreviewList is null) return;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _preview.Clear();
        RenamePlan = null;

        if (!int.TryParse(StartingNumberBox.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                out var startingNumber) || startingNumber < 1)
        {
            SetValidation("Informe um número inicial maior que zero.");
            return;
        }

        try
        {
            var plan = SectionAutomaticNaming.CreatePlan(_project, PrefixBox.Text, startingNumber);
            foreach (var item in plan) _preview.Add(item);
            PreviewSummary.Text = $"{plan.Count} trecho(s) na sequência global";
            ValidationText.Text = string.Empty;
            ApplyButton.IsEnabled = plan.Count > 0;
        }
        catch (ArgumentException ex)
        {
            SetValidation(ex.Message);
        }
    }

    private void SetValidation(string message)
    {
        PreviewSummary.Text = string.Empty;
        ValidationText.Text = message;
        ApplyButton.IsEnabled = false;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        RefreshPreview();
        if (!ApplyButton.IsEnabled) return;

        RenamePlan = _preview.ToList();
        DialogResult = true;
        Close();
    }
}
