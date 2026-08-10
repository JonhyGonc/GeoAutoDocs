using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;

namespace GeoAutoDocs.App;

public partial class MainWindow : Window
{
    private readonly IProjectStore _store;
    private readonly LocalLogger _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly ObservableCollection<Project> _projects = [];
    private Project? _currentProject;
    private bool _loadingFields;
    private bool _hasPendingChanges;
    private bool _closingAfterSave;

    public MainWindow(IProjectStore store, LocalLogger logger)
    {
        InitializeComponent();
        _store = store;
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
}

