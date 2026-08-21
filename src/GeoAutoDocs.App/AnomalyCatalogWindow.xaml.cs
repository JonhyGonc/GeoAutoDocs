using System.Windows;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.App;

public partial class AnomalyCatalogWindow : Window
{
    private readonly IAnomalyCatalogService _service;
    private readonly bool _readOnly;
    private List<AnomalyDefinition> _items = [];
    private AnomalyDefinition? _selected;

    public AnomalyCatalogWindow(IAnomalyCatalogService service, bool readOnly)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 980, 700);
        _service = service;
        _readOnly = readOnly;
        EditorPanel.IsEnabled = !readOnly;
        NewButton.IsEnabled = UpButton.IsEnabled = DownButton.IsEnabled = !readOnly;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync(Guid? select = null)
    {
        _items = (await _service.ListAsync()).ToList();
        ApplyFilter();
        if (select is not null) CatalogList.SelectedItem = _items.FirstOrDefault(x => x.Id == select);
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        CatalogList.ItemsSource = _items.Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void Catalog_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = CatalogList.SelectedItem as AnomalyDefinition;
        if (_selected is null) return;
        NameBox.Text = _selected.Name; TemplateBox.Text = _selected.DescriptionTemplate;
        PositionCheck.IsChecked = _selected.SupportsPosition; OrientationCheck.IsChecked = _selected.SupportsOrientation;
        ExtensionCheck.IsChecked = _selected.SupportsExtension; DimensionCheck.IsChecked = _selected.SupportsDimension;
        ConditionCheck.IsChecked = _selected.SupportsCondition; InfiltrationCheck.IsChecked = _selected.SupportsInfiltrationCondition;
        PercentageCheck.IsChecked = _selected.SupportsPercentage; ComplementCheck.IsChecked = _selected.SupportsComplementaryText;
        ActiveButton.Content = _selected.IsActive ? "Desativar" : "Ativar";
        UpdatePreview();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        _selected = new AnomalyDefinition { IsBuiltIn = false, SupportsPosition = true, IsActive = true };
        NameBox.Text = string.Empty; TemplateBox.Text = "{nome}: {textoComplementar}.";
        PositionCheck.IsChecked = true; OrientationCheck.IsChecked = ExtensionCheck.IsChecked = DimensionCheck.IsChecked = false;
        ConditionCheck.IsChecked = InfiltrationCheck.IsChecked = PercentageCheck.IsChecked = ComplementCheck.IsChecked = false;
        ActiveButton.Content = "Desativar"; NameBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _readOnly) return;
        try
        {
            _selected.Name = NameBox.Text; _selected.DescriptionTemplate = TemplateBox.Text;
            _selected.SupportsPosition = PositionCheck.IsChecked == true; _selected.SupportsOrientation = OrientationCheck.IsChecked == true;
            _selected.SupportsExtension = ExtensionCheck.IsChecked == true; _selected.SupportsDimension = DimensionCheck.IsChecked == true;
            _selected.SupportsCondition = ConditionCheck.IsChecked == true; _selected.SupportsInfiltrationCondition = InfiltrationCheck.IsChecked == true;
            _selected.SupportsPercentage = PercentageCheck.IsChecked == true; _selected.SupportsComplementaryText = ComplementCheck.IsChecked == true;
            var saved = await _service.SaveAsync(_selected); await ReloadAsync(saved.Id);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Catálogo de anomalias", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Active_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _readOnly) return;
        await _service.SetActiveAsync(_selected.Id, !_selected.IsActive); await ReloadAsync(_selected.Id);
    }

    private async Task MoveAsync(int direction)
    {
        if (_selected is null || _readOnly) return;
        var index = _items.FindIndex(x => x.Id == _selected.Id); var target = index + direction;
        if (index < 0 || target < 0 || target >= _items.Count) return;
        (_items[index], _items[target]) = (_items[target], _items[index]);
        await _service.ReorderAsync(_items.Select(x => x.Id).ToList()); await ReloadAsync(_selected.Id);
    }

    private async void Up_Click(object sender, RoutedEventArgs e) => await MoveAsync(-1);
    private async void Down_Click(object sender, RoutedEventArgs e) => await MoveAsync(1);
    private void Template_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();
    private void UpdatePreview()
    {
        if (_selected is null) return;
        try { _selected.Name = NameBox.Text; _selected.DescriptionTemplate = TemplateBox.Text; PreviewText.Text = AnomalyTemplate.Preview(_selected); }
        catch (Exception ex) { PreviewText.Text = ex.Message; }
    }
}
