using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class MainWindow : Window
{
    private readonly IProjectStore _store;
    private readonly WordReportService _wordReportService;
    private readonly LocalLogger _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly ObservableCollection<Project> _projects = [];
    private Project? _currentProject;
    private bool _loadingFields;
    private bool _hasPendingChanges;
    private bool _closingAfterSave;

    public MainWindow(IProjectStore store, WordReportService wordReportService, LocalLogger logger)
    {
        InitializeComponent();
        _store = store;
        _wordReportService = wordReportService;
        _logger = logger;
        ProjectsList.ItemsSource = _projects;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SavePendingAsync(); };
        Loaded += async (_, _) => await ReloadProjectsAsync();
        Closing += OnClosing;
    }

    private async Task ReloadProjectsAsync()
    {
        try
        {
            _projects.Clear();
            foreach (var project in await _store.ListAsync()) _projects.Add(project);
        }
        catch (Exception ex) { ShowError("Não foi possível carregar os projetos.", ex); }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Ask(this, "Novo projeto", "Nome do projeto:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await OpenEditorAsync((await _store.CreateAsync(name)).Id); }
        catch (Exception ex) { ShowError("Não foi possível criar o projeto.", ex); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Guid id) await OpenEditorAsync(id);
    }

    private async Task OpenEditorAsync(Guid id)
    {
        try
        {
            _currentProject = await _store.GetAsync(id);
            if (_currentProject is null) return;
            LoadFields(_currentProject);
            ProjectsView.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { ShowError("Não foi possível abrir o projeto.", ex); }
    }

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Guid id) return;
        var project = await _store.GetAsync(id);
        if (project is null) return;
        var name = TextPromptWindow.Ask(this, "Renomear projeto", "Novo nome:", project.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { project.Name = name; await _store.SaveAsync(project); await ReloadProjectsAsync(); }
        catch (Exception ex) { ShowError("Não foi possível renomear o projeto.", ex); }
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Guid id) return;
        var project = _projects.FirstOrDefault(x => x.Id == id);
        if (project is null || MessageBox.Show($"Excluir o projeto ‘{project.Name}’?", "Confirmar exclusão",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _store.DeleteAsync(id); await ReloadProjectsAsync(); }
        catch (Exception ex) { ShowError("Não foi possível excluir o projeto.", ex); }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        await SavePendingAsync();
        _currentProject = null;
        EditorView.Visibility = Visibility.Collapsed;
        ProjectsView.Visibility = Visibility.Visible;
        await ReloadProjectsAsync();
    }

    private void LoadFields(Project project)
    {
        _loadingFields = true;
        ProjectTitle.Text = project.Name;
        var report = project.ReportConfiguration;
        ClientBox.Text = report.Client; AreaBox.Text = report.Area; ContractBox.Text = report.Contract;
        PurchaseOrderBox.Text = report.PurchaseOrder; ReportCodeBox.Text = report.ReportCode;
        ReportDatePicker.SelectedDate = report.ReportDate; LocationBox.Text = report.LocationDescription;
        MetersBox.Text = report.TotalInvestigatedMeters?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
        TemplateBox.Text = report.TemplatePath ?? ""; LogoBox.Text = report.LogoPath ?? "";
        LocationImageBox.Text = report.LocationImagePath ?? "";
        EndoscopeCheck.IsChecked = report.Equipments.HasFlag(EquipmentSelection.Endoscope);
        BugCheck.IsChecked = report.Equipments.HasFlag(EquipmentSelection.Bug);
        HunterCheck.IsChecked = report.Equipments.HasFlag(EquipmentSelection.Hunter);
        SpiderCheck.IsChecked = report.Equipments.HasFlag(EquipmentSelection.Spider);
        TrackerCheck.IsChecked = report.Equipments.HasFlag(EquipmentSelection.Tracker);
        D300Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm300);
        D400Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm400);
        D500Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm500);
        D600Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm600);
        D800Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm800);
        D1000Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm1000);
        D1500Check.IsChecked = report.Diameters.HasFlag(StandardDiameterSelection.Mm1500);
        CustomDiametersBox.Text = report.CustomDiameters;
        OpeningCheck.IsChecked = report.IncludeInspectionOpening; GprCheck.IsChecked = report.IncludeGpr;
        CustomSectionsList.ItemsSource = project.CustomSections;
        GeneratedReportText.Text = report.GeneratedReportPath ?? "";
        OpenReportButton.IsEnabled = File.Exists(report.GeneratedReportPath);
        SaveStatus.Text = $"Salvo automaticamente • {project.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm}";
        _hasPendingChanges = false;
        _loadingFields = false;
    }

    private void FieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingFields || _currentProject is null) return;
        _hasPendingChanges = true;
        SaveStatus.Text = "Alterações pendentes...";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SavePendingAsync()
    {
        _saveTimer.Stop();
        if (!_hasPendingChanges || _currentProject is null) return;
        try
        {
            var report = _currentProject.ReportConfiguration;
            report.Client = ClientBox.Text.Trim(); report.Area = AreaBox.Text.Trim();
            report.Contract = ContractBox.Text.Trim(); report.PurchaseOrder = PurchaseOrderBox.Text.Trim();
            report.ReportCode = ReportCodeBox.Text.Trim(); report.ReportDate = ReportDatePicker.SelectedDate;
            report.LocationDescription = LocationBox.Text.Trim();
            report.TotalInvestigatedMeters = decimal.TryParse(MetersBox.Text, NumberStyles.Number,
                CultureInfo.CurrentCulture, out var meters) ? meters : null;
            report.TemplatePath = EmptyToNull(TemplateBox.Text); report.LogoPath = EmptyToNull(LogoBox.Text);
            report.LocationImagePath = EmptyToNull(LocationImageBox.Text);
            report.Equipments = EquipmentSelection.None
                | (EndoscopeCheck.IsChecked == true ? EquipmentSelection.Endoscope : 0)
                | (BugCheck.IsChecked == true ? EquipmentSelection.Bug : 0)
                | (HunterCheck.IsChecked == true ? EquipmentSelection.Hunter : 0)
                | (SpiderCheck.IsChecked == true ? EquipmentSelection.Spider : 0)
                | (TrackerCheck.IsChecked == true ? EquipmentSelection.Tracker : 0);
            report.Diameters = StandardDiameterSelection.None
                | (D300Check.IsChecked == true ? StandardDiameterSelection.Mm300 : 0)
                | (D400Check.IsChecked == true ? StandardDiameterSelection.Mm400 : 0)
                | (D500Check.IsChecked == true ? StandardDiameterSelection.Mm500 : 0)
                | (D600Check.IsChecked == true ? StandardDiameterSelection.Mm600 : 0)
                | (D800Check.IsChecked == true ? StandardDiameterSelection.Mm800 : 0)
                | (D1000Check.IsChecked == true ? StandardDiameterSelection.Mm1000 : 0)
                | (D1500Check.IsChecked == true ? StandardDiameterSelection.Mm1500 : 0);
            report.CustomDiameters = CustomDiametersBox.Text.Trim();
            report.IncludeInspectionOpening = OpeningCheck.IsChecked == true;
            report.IncludeGpr = GprCheck.IsChecked == true;
            await _store.SaveAsync(_currentProject);
            _hasPendingChanges = false;
            SaveStatus.Text = $"Salvo automaticamente • {_currentProject.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Falha ao salvar";
            ShowError("Não foi possível salvar as alterações.", ex);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterSave || !_hasPendingChanges) return;
        e.Cancel = true;
        await SavePendingAsync();
        if (_hasPendingChanges) return;
        _closingAfterSave = true;
        Close();
    }

    private void ShowError(string message, Exception ex)
    {
        _logger.Error(message, ex);
        MessageBox.Show(message, "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SelectTemplate_Click(object sender, RoutedEventArgs e) => SelectFile(TemplateBox, "Documento Word (*.docx)|*.docx");
    private void SelectLogo_Click(object sender, RoutedEventArgs e) => SelectFile(LogoBox, "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");
    private void SelectLocationImage_Click(object sender, RoutedEventArgs e) => SelectFile(LocationImageBox, "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");

    private void SelectFile(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        target.Text = dialog.FileName;
        ScheduleSave();
    }

    private void AddCustomSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        var dialog = new CustomSectionWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var section = new CustomSection
        {
            ProjectId = _currentProject.Id,
            Title = EmptyToNull(dialog.SectionTitle),
            Text = dialog.SectionText,
            Order = _currentProject.CustomSections.Count
        };
        section.Images.AddRange(dialog.ImagePaths.Select((path, index) => new CustomSectionImage
        {
            CustomSectionId = section.Id,
            FilePath = path,
            Order = index
        }));
        _currentProject.CustomSections.Add(section);
        CustomSectionsList.Items.Refresh();
        ScheduleSave();
    }

    private void RemoveCustomSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || ((Button)sender).Tag is not Guid id) return;
        var section = _currentProject.CustomSections.SingleOrDefault(x => x.Id == id);
        if (section is null) return;
        _currentProject.CustomSections.Remove(section);
        CustomSectionsList.Items.Refresh();
        ScheduleSave();
    }

    private async void GenerateReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        await SavePendingAsync();
        try
        {
            GeneratedReportText.Text = "Gerando relatório...";
            var path = await _wordReportService.GenerateAsync(_currentProject, includeSection4TechnicalProof: true);
            await _store.SaveAsync(_currentProject);
            GeneratedReportText.Text = path;
            OpenReportButton.IsEnabled = true;
            MessageBox.Show("Relatório de teste gerado com sucesso.", "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            GeneratedReportText.Text = "Falha ao gerar relatório.";
            ShowError(ex.Message, ex);
        }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentProject?.ReportConfiguration.GeneratedReportPath;
        if (!File.Exists(path))
        {
            MessageBox.Show("O relatório gerado não foi encontrado.", "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
    }

    private void ScheduleSave()
    {
        if (_loadingFields || _currentProject is null) return;
        _hasPendingChanges = true;
        SaveStatus.Text = "Alterações pendentes...";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
