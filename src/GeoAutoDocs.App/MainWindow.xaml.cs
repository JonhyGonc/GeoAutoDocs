using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class MainWindow : Window
{
    private readonly IProjectStore _store;
    private readonly WordReportService _wordReportService;
    private readonly SharedProjectService _sharedProjects;
    private readonly LocalLogger _logger;
    private readonly FrameCaptureService _frameCaptureService;
    private readonly OccurrenceService _occurrenceService;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _playerTimer;
    private readonly ObservableCollection<Project> _projects = [];
    private Project? _currentProject;
    private Report? _currentReport;
    private SharedProjectSession? _sharedSession;
    private Location? _currentLocation;
    private InspectionSection? _currentSection;
    private VideoReference? _currentVideo;
    private Media? _currentMedia;
    private FrameCaptureDraft? _captureDraft;
    private bool _loadingFields;
    private bool _hasPendingChanges;
    private bool _sharedDirty;
    private bool _closingAfterSave;
    private bool _saveErrorNotified;
    private bool _seeking;
    private bool _playerErrorNotified;
    private bool _playerDisposed;
    private bool _loadingInspections;
    private Point _occurrenceDragStart;
    private Guid? _projectDialogProjectId;
    private OccurrenceWindow? _embeddedOccurrenceEditor;

    public MainWindow(IProjectStore store, WordReportService wordReportService,
        SharedProjectService sharedProjects, LocalLogger logger, FrameCaptureService frameCaptureService,
        OccurrenceService occurrenceService)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 1480, 900);
        _store = store;
        _wordReportService = wordReportService;
        _sharedProjects = sharedProjects;
        _logger = logger;
        _frameCaptureService = frameCaptureService;
        _occurrenceService = occurrenceService;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--no-audio");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        var version = typeof(App).Assembly.GetName().Version;
        SidebarVersionText.Text = version is null ? "Versão local" : $"Versão {version.Major}.{version.Minor}";
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => HandlePlayerError());
        _mediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            VideoEmptyState.Visibility = Visibility.Collapsed;
            VideoLoadedBadge.Visibility = Visibility.Visible;
            PlayPauseButton.Content = "❚❚";
        });
        _mediaPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(() => PlayPauseButton.Content = "▶");
        _mediaPlayer.Stopped += (_, _) => Dispatcher.BeginInvoke(() => PlayPauseButton.Content = "▶");
        ProjectsList.ItemsSource = _projects;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SavePendingAsync(); };
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _syncTimer.Tick += async (_, _) => await CheckpointSharedAsync();
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _heartbeatTimer.Tick += async (_, _) => await HeartbeatAsync();
        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playerTimer.Tick += (_, _) => RefreshPlayerPosition();
        _playerTimer.Start();
        SizeChanged += (_, _) => ApplyDashboardDensity();
        Loaded += async (_, _) => await ReloadProjectsAsync();
        Closing += OnClosing;
    }

    private async Task ReloadProjectsAsync()
    {
        try
        {
            _projects.Clear();
            foreach (var project in await _store.ListAsync()) _projects.Add(project);
            CollectionViewSource.GetDefaultView(_projects).Refresh();
            RefreshDashboardMetrics();
            RefreshProjectsEmptyState();
        }
        catch (Exception ex) { ShowError("Não foi possível carregar os projetos.", ex); }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        ResetNewProjectForm();
        HomeShell.Visibility = Visibility.Visible;
        ProjectsView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Collapsed;
        NewProjectView.Visibility = Visibility.Visible;
        NewProjectNameBox.Focus();
    }

    private void CancelNewProject_Click(object sender, RoutedEventArgs e)
    {
        NewProjectView.Visibility = Visibility.Collapsed;
        ProjectsView.Visibility = Visibility.Visible;
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewProjectNameBox.Text))
        {
            NewProjectNameBox.Focus();
            return;
        }
        try
        {
            var project = await _store.CreateAsync(NewProjectNameBox.Text.Trim());
            project.DefaultClient = NewProjectClientBox.Text.Trim();
            project.DefaultCnpj = NewProjectCnpjBox.Text.Trim();
            project.DefaultArea = NewProjectAreaBox.Text.Trim();
            project.DefaultContract = NewProjectContractBox.Text.Trim();
            project.DefaultPurchaseOrder = NewProjectPurchaseOrderBox.Text.Trim();
            project.DefaultLogoPath = EmptyToNull(NewProjectLogoBox.Text);
            await _store.SaveAsync(project);
            NewProjectView.Visibility = Visibility.Collapsed;
            await OpenEditorAsync(project.Id);
        }
        catch (Exception ex) { ShowError("Não foi possível criar o projeto.", ex); }
    }

    private void SelectNewProjectLogo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Title = "Selecione o logo do cliente"
        };
        if (dialog.ShowDialog(this) != true) return;
        NewProjectLogoBox.Text = dialog.FileName;
        NewProjectLogoPreview.Source = LoadThumbnail(dialog.FileName);
        NewProjectLogoStatus.Text = Path.GetFileName(dialog.FileName);
    }

    private void ResetNewProjectForm()
    {
        NewProjectNameBox.Clear(); NewProjectClientBox.Clear(); NewProjectCnpjBox.Clear();
        NewProjectAreaBox.Clear(); NewProjectContractBox.Clear(); NewProjectPurchaseOrderBox.Clear();
        NewProjectLogoBox.Clear(); NewProjectLogoPreview.Source = null; NewProjectLogoStatus.Text = "Sem imagem";
    }

    private void ProjectsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ProjectsSearchBox.Text.Trim();
        ProjectsSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var view = CollectionViewSource.GetDefaultView(_projects);
        view.Filter = item => item is Project project &&
            (query.Length == 0 || project.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.DefaultClient?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
        view.Refresh();
        RefreshProjectsEmptyState();
    }

    private void RefreshProjectsEmptyState()
    {
        var query = ProjectsSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_projects);
        ProjectsEmptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ProjectsListPanel.Visibility = view.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        ProjectsEmptyTitle.Text = query.Length == 0 ? "Nenhum projeto criado" : "Nenhum projeto encontrado";
        ProjectsEmptyDescription.Text = query.Length == 0
            ? "Crie seu primeiro projeto para começar."
            : "Tente buscar por outro nome de projeto ou cliente.";
        ProjectsEmptyAction.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewProjectTopButton.Visibility = _projects.Count == 0 && query.Length == 0
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshDashboardMetrics()
    {
        var projectCount = _projects.Count;
        var reportCount = _projects.Sum(project => project.Reports.Count);
        var inProgressCount = _projects.Sum(project =>
            project.Reports.Count(report => string.IsNullOrWhiteSpace(report.GeneratedReportPath)));
        var sharedCount = _projects.Count(project => project.StorageKind == ProjectStorageKind.Shared);

        TotalProjectsMetric.Text = projectCount.ToString(CultureInfo.CurrentCulture);
        TotalProjectsDetail.Text = projectCount switch
        {
            0 => "Nenhum projeto cadastrado",
            1 => "1 projeto cadastrado",
            _ => $"{projectCount} projetos cadastrados"
        };
        TotalReportsMetric.Text = reportCount.ToString(CultureInfo.CurrentCulture);
        TotalReportsDetail.Text = reportCount switch
        {
            0 => "Nenhum relatório",
            1 => "1 relatório no sistema",
            _ => $"{reportCount} relatórios no sistema"
        };
        InProgressReportsMetric.Text = inProgressCount.ToString(CultureInfo.CurrentCulture);
        InProgressReportsDetail.Text = inProgressCount switch
        {
            0 => "Nenhum em andamento",
            1 => "1 relatório sem arquivo final",
            _ => $"{inProgressCount} relatórios sem arquivo final"
        };
        SharedProjectsMetric.Text = sharedCount.ToString(CultureInfo.CurrentCulture);
        SharedProjectsDetail.Text = sharedCount switch
        {
            0 => "Somente projetos locais",
            1 => "1 projeto compartilhado",
            _ => $"{sharedCount} projetos compartilhados"
        };
    }

    private void ApplyDashboardDensity()
    {
        var compact = ActualWidth > 0 && ActualWidth < 1100;
        var padding = compact ? new Thickness(8, 16, 8, 16) : new Thickness(18, 16, 18, 16);
        foreach (var card in new[] { TotalProjectsCard, TotalReportsCard, InProgressReportsCard, SharedProjectsCard })
            card.Padding = padding;
        foreach (var column in new[] { TotalProjectsIconColumn, TotalReportsIconColumn, InProgressReportsIconColumn, SharedProjectsIconColumn })
            column.Width = new GridLength(compact ? 44 : 54);
        foreach (var label in new[] { TotalProjectsLabel, TotalReportsLabel, InProgressReportsLabel, SharedProjectsLabel })
            label.FontSize = compact ? 9.5 : 13;
        foreach (var detail in new[] { TotalProjectsDetail, TotalReportsDetail, InProgressReportsDetail, SharedProjectsDetail })
            detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id }) await OpenEditorAsync(id);
    }

    private async void ProjectCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (sender is FrameworkElement { Tag: Guid id }) await OpenEditorAsync(id);
    }

    private void ProjectActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: not null } button) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async Task OpenEditorAsync(Guid id)
    {
        try
        {
            var project = await _store.GetAsync(id);
            if (project is null) return;
            _sharedSession = null;
            if (project.StorageKind == ProjectStorageKind.Shared && !string.IsNullOrWhiteSpace(project.SharedProjectPath))
                project = await OpenSharedProjectAsync(project);
            var templateChanged = _wordReportService.ApplyStandardTemplate(project);
            if (templateChanged && project.StorageKind == ProjectStorageKind.Local)
                await _store.SaveAsync(project);
            _currentProject = project;
            LoadProject(project);
            ProjectsView.Visibility = Visibility.Collapsed;
            NewProjectView.Visibility = Visibility.Collapsed;
            HomeShell.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Visible;
            SetSharedTimers(_sharedSession is { IsReadOnly: false });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Não foi possível abrir o projeto.", ex); }
    }

    private async Task<Project> OpenSharedProjectAsync(Project project)
    {
        try
        {
            _sharedSession = await _sharedProjects.OpenAsync(project, project.SharedProjectPath!);
        }
        catch (SharedProjectLockedException ex)
        {
            var stale = DateTime.UtcNow - ex.ProjectLock.LastHeartbeat > SharedProjectService.DefaultStaleAfter;
            if (stale && MessageBox.Show(
                    $"Bloqueio antigo de {ex.ProjectLock.UserName} em {ex.ProjectLock.ComputerName}.\nLiberar bloqueio antigo?",
                    "Projeto compartilhado", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _sharedSession = await _sharedProjects.OpenAsync(project, project.SharedProjectPath!, recoverStaleLock: true);
            else if (MessageBox.Show(
                         $"Projeto em edição por {ex.ProjectLock.UserName} em {ex.ProjectLock.ComputerName}.\nAbrir somente para leitura?",
                         "Projeto compartilhado", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                _sharedSession = await _sharedProjects.OpenAsync(project, project.SharedProjectPath!, readOnly: true);
            else
                throw new OperationCanceledException();
        }
        return _sharedSession.Project;
    }

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        try
        {
            var project = await _store.GetAsync(id);
            if (project is null) return;
            _projectDialogProjectId = id;
            RenameProjectNameBox.Text = project.Name;
            RenameProjectValidationText.Visibility = Visibility.Collapsed;
            DeleteProjectDialogPanel.Visibility = Visibility.Collapsed;
            RenameProjectDialogPanel.Visibility = Visibility.Visible;
            ProjectDialogOverlay.Visibility = Visibility.Visible;
            RenameProjectNameBox.Focus();
            RenameProjectNameBox.SelectAll();
        }
        catch (Exception ex) { ShowError("Não foi possível carregar o projeto para renomear.", ex); }
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        var project = _projects.FirstOrDefault(x => x.Id == id);
        if (project is null) return;
        _projectDialogProjectId = id;
        DeleteProjectMessage.Text = $"“{project.Name}” será removido.";
        RenameProjectDialogPanel.Visibility = Visibility.Collapsed;
        DeleteProjectDialogPanel.Visibility = Visibility.Visible;
        ProjectDialogOverlay.Visibility = Visibility.Visible;
    }

    private async void ConfirmRenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDialogProjectId is not Guid id) return;
        var name = RenameProjectNameBox.Text.Trim();
        if (name.Length == 0)
        {
            RenameProjectValidationText.Visibility = Visibility.Visible;
            RenameProjectNameBox.Focus();
            return;
        }

        try
        {
            var project = await _store.GetAsync(id);
            if (project is null) { CloseProjectDialog(); return; }
            project.Name = name;
            await _store.SaveAsync(project);
            CloseProjectDialog();
            await ReloadProjectsAsync();
        }
        catch (Exception ex) { ShowError("Não foi possível renomear o projeto.", ex); }
    }

    private async void ConfirmDeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectDialogProjectId is not Guid id) return;
        try
        {
            await _store.DeleteAsync(id);
            CloseProjectDialog();
            await ReloadProjectsAsync();
        }
        catch (Exception ex) { ShowError("Não foi possível excluir o projeto.", ex); }
    }

    private void CancelProjectDialog_Click(object sender, RoutedEventArgs e) => CloseProjectDialog();

    private void CloseProjectDialog()
    {
        ProjectDialogOverlay.Visibility = Visibility.Collapsed;
        RenameProjectDialogPanel.Visibility = Visibility.Collapsed;
        DeleteProjectDialogPanel.Visibility = Visibility.Collapsed;
        RenameProjectValidationText.Visibility = Visibility.Collapsed;
        _projectDialogProjectId = null;
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!await FinalizeCurrentProjectAsync()) return;
        StopAndReleaseCurrentMedia();
        _currentProject = null; _currentReport = null; _sharedSession = null;
        EditorView.Visibility = Visibility.Collapsed;
        HomeShell.Visibility = Visibility.Visible;
        NewProjectView.Visibility = Visibility.Collapsed;
        ProjectsView.Visibility = Visibility.Visible;
        await ReloadProjectsAsync();
    }

    private void LoadProject(Project project)
    {
        _loadingFields = true;
        ProjectTitle.Text = project.Name;
        ProjectClientSummary.Text = string.IsNullOrWhiteSpace(project.DefaultClient)
            ? "Dados do cliente ainda não informados" : project.DefaultClient;
        DefaultClientBox.Text = project.DefaultClient; DefaultCnpjBox.Text = project.DefaultCnpj;
        DefaultAreaBox.Text = project.DefaultArea; DefaultContractBox.Text = project.DefaultContract;
        DefaultPurchaseOrderBox.Text = project.DefaultPurchaseOrder; DefaultLogoBox.Text = project.DefaultLogoPath ?? "";
        var reports = project.Reports.OrderBy(x => x.Order).ToList();
        ReportsCombo.ItemsSource = reports;
        ReportsList.ItemsSource = reports;
        _currentReport = project.Reports.OrderBy(x => x.Order).FirstOrDefault();
        ReportsCombo.SelectedItem = _currentReport;
        ReportsList.SelectedItem = _currentReport;
        StorageStatus.Text = project.StorageKind == ProjectStorageKind.Shared
            ? (_sharedSession?.IsReadOnly == true ? "Compartilhado • somente leitura" : "Compartilhado • editor com lock")
            : "Local";
        ShareProjectButton.IsEnabled = project.StorageKind == ProjectStorageKind.Local;
        NewReportButton.IsEnabled = _sharedSession?.IsReadOnly != true;
        HeaderNewReportButton.IsEnabled = _sharedSession?.IsReadOnly != true;
        ReportsEmptyState.Visibility = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReportHeaderTab.IsEnabled = !IsReadOnly && _currentReport is not null;
        WordConfigTab.IsEnabled = true;
        ReadOnlyInspectionText.Visibility = IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        RefreshInspectionTree();
        UpdateInspectionWriteState();
        LoadReport(_currentReport);
        RefreshFilePreviews();
        RefreshOverview();
        OverviewTab.IsSelected = true;
        SaveStatus.Text = "✓ Salvo";
        _hasPendingChanges = false;
        _loadingFields = false;
    }

    private void LoadReport(Report? report)
    {
        _loadingFields = true;
        var enabled = report is not null;
        ReportHeaderTab.IsEnabled = enabled && !IsReadOnly;
        WordConfigTab.IsEnabled = true;
        ClientBox.Text = report?.Client ?? ""; CnpjBox.Text = report?.Cnpj ?? "";
        AreaBox.Text = report?.Area ?? ""; ContractBox.Text = report?.Contract ?? "";
        PurchaseOrderBox.Text = report?.PurchaseOrder ?? ""; ReportCodeBox.Text = report?.DisplayCode ?? "";
        ReportDatePicker.SelectedDate = report?.ReportDate;
        MetersBox.Text = report?.TotalInvestigatedMeters?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
        TemplateBox.Text = report?.TemplatePath ?? ""; LogoBox.Text = report?.LogoPath ?? "";
        LocationImageBox.Text = report?.LocationImagePath ?? "";
        ActiveReportTitle.Text = report?.DisplayCode ?? "Selecione um relatório";
        EndoscopeCheck.IsChecked = report?.Equipments.HasFlag(EquipmentSelection.Endoscope) == true;
        BugCheck.IsChecked = report?.Equipments.HasFlag(EquipmentSelection.Bug) == true;
        HunterCheck.IsChecked = report?.Equipments.HasFlag(EquipmentSelection.Hunter) == true;
        SpiderCheck.IsChecked = report?.Equipments.HasFlag(EquipmentSelection.Spider) == true;
        TrackerCheck.IsChecked = report?.Equipments.HasFlag(EquipmentSelection.Tracker) == true;
        D300Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm300) == true;
        D400Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm400) == true;
        D500Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm500) == true;
        D600Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm600) == true;
        D800Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm800) == true;
        D1000Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm1000) == true;
        D1500Check.IsChecked = report?.Diameters.HasFlag(StandardDiameterSelection.Mm1500) == true;
        CustomDiametersBox.Text = report?.CustomDiameters ?? "";
        OtherDiameterToggle.IsChecked = !string.IsNullOrWhiteSpace(report?.CustomDiameters);
        CustomDiameterPanel.Visibility = OtherDiameterToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OpeningCheck.IsChecked = report?.IncludeInspectionOpening == true; GprCheck.IsChecked = report?.IncludeGpr == true;
        RefreshDevelopmentImageSummary();
        CustomSectionsList.ItemsSource = report?.CustomSections;
        RefreshReportSectionSelection();
        GeneratedReportText.Text = File.Exists(report?.GeneratedReportPath)
            ? $"✓ Relatório gerado\n{Path.GetFileName(report!.GeneratedReportPath)}" : "";
        OpenReportButton.IsEnabled = File.Exists(report?.GeneratedReportPath);
        OpenReportFolderButton.IsEnabled = File.Exists(report?.GeneratedReportPath);
        RefreshFilePreviews();
        _loadingFields = false;
        RefreshReportReview();
    }

    private void ReportsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFields || _currentProject is null) return;
        ApplyFieldsToModel();
        _currentReport = ReportsCombo.SelectedItem as Report;
        ReportsList.SelectedItem = _currentReport;
        LoadReport(_currentReport);
    }

    private void ReportsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFields || _currentProject is null) return;
        ApplyFieldsToModel();
        _currentReport = ReportsList.SelectedItem as Report;
        _loadingFields = true;
        ReportsCombo.SelectedItem = _currentReport;
        _loadingFields = false;
        LoadReport(_currentReport);
    }

    private async void NewReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _sharedSession?.IsReadOnly == true) return;
        ApplyFieldsToModel();
        var created = _currentProject.CreateReport();
        created.TemplatePath = _wordReportService.DefaultTemplatePath;
        _loadingFields = true;
        var reports = _currentProject.Reports.OrderBy(x => x.Order).ToList();
        ReportsCombo.ItemsSource = reports;
        ReportsList.ItemsSource = reports;
        ReportsEmptyState.Visibility = Visibility.Collapsed;
        _currentReport = created;
        ReportsCombo.SelectedItem = created;
        ReportsList.SelectedItem = created;
        _loadingFields = false;
        LoadReport(_currentReport);
        WordConfigTab.IsSelected = true;
        _hasPendingChanges = true;
        await SavePendingAsync();
        ReportCodeBox.Focus();
        ReportCodeBox.SelectAll();
    }

    private void ContinueInspection_Click(object sender, RoutedEventArgs e) => InspectionsTab.IsSelected = true;

    private void OverviewNewReport_Click(object sender, RoutedEventArgs e)
    {
        WordConfigTab.IsSelected = true;
        NewReport_Click(sender, e);
    }

    private void OpenOverviewReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || sender is not Button { Tag: Guid reportId }) return;
        ApplyFieldsToModel();
        var report = _currentProject.Reports.FirstOrDefault(x => x.Id == reportId);
        if (report is null) return;
        _currentReport = report;
        _loadingFields = true;
        ReportsCombo.SelectedItem = report;
        ReportsList.SelectedItem = report;
        _loadingFields = false;
        LoadReport(report);
        WordConfigTab.IsSelected = true;
    }

    private void FieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingFields || _currentProject is null || _sharedSession?.IsReadOnly == true) return;
        _hasPendingChanges = true;
        SaveStatus.Text = "Salvando...";
        if (sender == OpeningCheck || sender == GprCheck) RefreshDevelopmentImageSummary();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void OtherDiameterToggle_Changed(object sender, RoutedEventArgs e)
    {
        CustomDiameterPanel.Visibility = OtherDiameterToggle.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingFields) return;
        if (OtherDiameterToggle.IsChecked != true) CustomDiametersBox.Clear();
        FieldChanged(sender, e);
        if (OtherDiameterToggle.IsChecked == true) CustomDiametersBox.Focus();
    }

    private void ApplyFieldsToModel()
    {
        if (_currentProject is null) return;
        _currentProject.DefaultClient = DefaultClientBox.Text.Trim(); _currentProject.DefaultCnpj = DefaultCnpjBox.Text.Trim();
        _currentProject.DefaultArea = DefaultAreaBox.Text.Trim(); _currentProject.DefaultContract = DefaultContractBox.Text.Trim();
        _currentProject.DefaultPurchaseOrder = DefaultPurchaseOrderBox.Text.Trim(); _currentProject.DefaultLogoPath = EmptyToNull(DefaultLogoBox.Text);
        if (_currentReport is null) return;
        var report = _currentReport;
        report.Client = _currentProject.DefaultClient; report.Cnpj = _currentProject.DefaultCnpj;
        report.Area = _currentProject.DefaultArea; report.Contract = _currentProject.DefaultContract;
        report.PurchaseOrder = _currentProject.DefaultPurchaseOrder; report.LogoPath = _currentProject.DefaultLogoPath;
        report.ReportCode = ReportCodeBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(report.ReportCode)) report.Name = report.ReportCode;
        report.ReportDate = ReportDatePicker.SelectedDate;
        report.LocationDescription = report.Area;
        report.TotalInvestigatedMeters = decimal.TryParse(MetersBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var meters) ? meters : null;
        report.TemplatePath = EmptyToNull(TemplateBox.Text); report.LocationImagePath = EmptyToNull(LocationImageBox.Text);
        report.Equipments = EquipmentSelection.None | (EndoscopeCheck.IsChecked == true ? EquipmentSelection.Endoscope : 0) | (BugCheck.IsChecked == true ? EquipmentSelection.Bug : 0) | (HunterCheck.IsChecked == true ? EquipmentSelection.Hunter : 0) | (SpiderCheck.IsChecked == true ? EquipmentSelection.Spider : 0) | (TrackerCheck.IsChecked == true ? EquipmentSelection.Tracker : 0);
        report.Diameters = StandardDiameterSelection.None | (D300Check.IsChecked == true ? StandardDiameterSelection.Mm300 : 0) | (D400Check.IsChecked == true ? StandardDiameterSelection.Mm400 : 0) | (D500Check.IsChecked == true ? StandardDiameterSelection.Mm500 : 0) | (D600Check.IsChecked == true ? StandardDiameterSelection.Mm600 : 0) | (D800Check.IsChecked == true ? StandardDiameterSelection.Mm800 : 0) | (D1000Check.IsChecked == true ? StandardDiameterSelection.Mm1000 : 0) | (D1500Check.IsChecked == true ? StandardDiameterSelection.Mm1500 : 0);
        report.CustomDiameters = CustomDiametersBox.Text.Trim(); report.IncludeInspectionOpening = OpeningCheck.IsChecked == true; report.IncludeGpr = GprCheck.IsChecked == true;
    }

    private async Task SavePendingAsync()
    {
        _saveTimer.Stop();
        if (!_hasPendingChanges || _currentProject is null || _sharedSession?.IsReadOnly == true) return;
        try
        {
            ApplyFieldsToModel();
            await _store.SaveAsync(_currentProject);
            if (_sharedSession is not null) { await _sharedSession.SaveLocalAsync(); _sharedDirty = true; }
            _hasPendingChanges = false;
            _saveErrorNotified = false;
            ProjectClientSummary.Text = string.IsNullOrWhiteSpace(_currentProject.DefaultClient)
                ? "Dados do cliente ainda não informados" : _currentProject.DefaultClient;
            ActiveReportTitle.Text = _currentReport?.DisplayCode ?? "Selecione um relatório";
            ReportsList.Items.Refresh();
            RefreshOverview();
            RefreshReportReview();
            SaveStatus.Text = "✓ Salvo";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Falha ao salvar — consulte o log";
            _logger.Error("Não foi possível salvar as alterações.", ex);
            if (_saveErrorNotified) return;
            _saveErrorNotified = true;
            MessageBox.Show("Não foi possível salvar as alterações. Consulte o log.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CheckpointSharedAsync()
    {
        if (_sharedSession is null || _sharedSession.IsReadOnly || !_sharedDirty) return;
        try
        {
            await SavePendingAsync(); await _sharedSession.CheckpointAsync(); _sharedDirty = false;
            SaveStatus.Text = "Sincronizado com o servidor";
        }
        catch (Exception ex) { SaveStatus.Text = "Servidor indisponível — alterações protegidas localmente"; _logger.Error("Falha no checkpoint compartilhado.", ex); }
    }

    private async Task HeartbeatAsync()
    {
        try { if (_sharedSession is { IsReadOnly: false }) await _sharedSession.HeartbeatAsync(); }
        catch (Exception ex) { SaveStatus.Text = "Servidor indisponível — alterações protegidas localmente"; _logger.Error("Falha no heartbeat.", ex); }
    }

    private async Task<bool> FinalizeCurrentProjectAsync()
    {
        try
        {
            _frameCaptureService.Discard(_captureDraft);
            _captureDraft = null;
            StopAndReleaseCurrentMedia();
            await SavePendingAsync();
            if (_sharedSession is not null)
            {
                await _sharedSession.CloseAsync();
                _sharedDirty = false;
            }
            SetSharedTimers(false);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("A sincronização final falhou. O cache local foi preservado.", ex);
            return false;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterSave || (_currentProject is null && !_hasPendingChanges))
        {
            DisposePlayer();
            return;
        }
        e.Cancel = true;
        if (!await FinalizeCurrentProjectAsync()) return;
        _closingAfterSave = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private async void ShareProject_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do projeto compartilhado", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ApplyFieldsToModel();
            _sharedSession = await _sharedProjects.OpenAsync(_currentProject, dialog.FolderName);
            _currentProject = _sharedSession.Project;
            await _store.SaveAsync(_currentProject);
            LoadProject(_currentProject); SetSharedTimers(true);
        }
        catch (Exception ex) { ShowError("Não foi possível compartilhar o projeto.", ex); }
    }

    private void SetSharedTimers(bool enabled)
    {
        _syncTimer.Stop(); _heartbeatTimer.Stop();
        if (enabled) { _syncTimer.Start(); _heartbeatTimer.Start(); }
    }

    private void SelectTemplate_Click(object sender, RoutedEventArgs e) => SelectFile(TemplateBox, "Documento Word (*.docx)|*.docx");
    private void SelectLogo_Click(object sender, RoutedEventArgs e) => SelectFile(LogoBox, "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");
    private void SelectLocationImage_Click(object sender, RoutedEventArgs e) => SelectFile(LocationImageBox, "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");
    private void SelectDefaultLogo_Click(object sender, RoutedEventArgs e) => SelectFile(DefaultLogoBox, "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");

    private void SelectDevelopmentImages_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null || IsReadOnly || sender is not Button { Tag: string groupName } ||
            !Enum.TryParse<DevelopmentImageGroup>(groupName, out var group)) return;
        var dialog = new OpenFileDialog
        {
            Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true, Multiselect = true,
            Title = "Selecione as imagens na ordem desejada"
        };
        if (dialog.ShowDialog(this) != true) return;
        _currentReport.DevelopmentImages.RemoveAll(x => x.Group == group);
        _currentReport.DevelopmentImages.AddRange(dialog.FileNames.Select((path, index) => new DevelopmentImage
        {
            ReportId = _currentReport.Id, Group = group, FilePath = path, Order = index
        }));
        RefreshDevelopmentImageSummary();
        ScheduleSave();
    }

    private void ClearDevelopmentImages_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null || IsReadOnly || sender is not Button { Tag: string groupName } ||
            !Enum.TryParse<DevelopmentImageGroup>(groupName, out var group)) return;
        _currentReport.DevelopmentImages.RemoveAll(x => x.Group == group);
        RefreshDevelopmentImageSummary();
        ScheduleSave();
    }

    private void RefreshDevelopmentImageSummary()
    {
        List<DevelopmentImagePreviewItem> Items(DevelopmentImageGroup group) => _currentReport?.DevelopmentImages
            .Where(x => x.Group == group).OrderBy(x => x.Order)
            .Select(x => new DevelopmentImagePreviewItem(x.FilePath)).ToList() ?? [];

        string Summary(DevelopmentImageGroup group)
        {
            var count = _currentReport?.DevelopmentImages.Count(x => x.Group == group) ?? 0;
            return count == 0 ? "Imagens padrão do modelo" : $"{count} imagem(ns) selecionada(s)";
        }
        VideoInspectionImagesList.ItemsSource = Items(DevelopmentImageGroup.VideoInspection);
        OpeningImagesList.ItemsSource = Items(DevelopmentImageGroup.InspectionOpening);
        GprImagesList.ItemsSource = Items(DevelopmentImageGroup.Gpr);
        VideoInspectionImagesText.Text = Summary(DevelopmentImageGroup.VideoInspection);
        OpeningImagesText.Text = Summary(DevelopmentImageGroup.InspectionOpening);
        GprImagesText.Text = Summary(DevelopmentImageGroup.Gpr);
        OpeningImagesPanel.Visibility = OpeningCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GprImagesPanel.Visibility = GprCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshReportReview();
    }

    private void SelectFile(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        target.Text = dialog.FileName;
        RefreshFilePreviews();
        ScheduleSave();
    }

    private void RefreshFilePreviews()
    {
        DefaultLogoPreview.Source = LoadBitmap(DefaultLogoBox.Text);
        var hasDefaultLogo = File.Exists(DefaultLogoBox.Text);
        DefaultLogoStatus.Text = hasDefaultLogo ? "Imagem selecionada" : "Nenhuma imagem selecionada";
        DefaultLogoButton.Content = hasDefaultLogo ? "Alterar imagem" : "Selecionar imagem";
        LocationImagePreview.Source = LoadBitmap(LocationImageBox.Text);
        LocationImageStatus.Text = File.Exists(LocationImageBox.Text) ? "Imagem selecionada" : "Nenhuma imagem selecionada";
    }

    private void AddCustomSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentReport is null || _sharedSession?.IsReadOnly == true) return;
        var dialog = new CustomSectionWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var section = new CustomSection { ProjectId = _currentProject.Id, ReportId = _currentReport.Id, Title = EmptyToNull(dialog.SectionTitle), Text = dialog.SectionText, Order = _currentReport.CustomSections.Count };
        section.Images.AddRange(dialog.ImagePaths.Select((path, index) => new CustomSectionImage { CustomSectionId = section.Id, FilePath = path, Order = index }));
        _currentReport.CustomSections.Add(section); CustomSectionsList.Items.Refresh(); ScheduleSave();
    }

    private void RemoveCustomSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null || ((Button)sender).Tag is not Guid id) return;
        var section = _currentReport.CustomSections.SingleOrDefault(x => x.Id == id);
        if (section is null) return;
        _currentReport.CustomSections.Remove(section); CustomSectionsList.Items.Refresh(); ScheduleSave();
    }

    private async void GenerateReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentReport is null) return;
        ApplyFieldsToModel();
        await SavePendingAsync();
        try
        {
            var validation = _wordReportService.Validate(_currentProject, _currentReport);
            RefreshReportReview(validation);
            if (validation.Errors.Count > 0 || validation.Warnings.Count > 0)
            {
                var validationWindow = new ReportValidationWindow(validation) { Owner = this };
                if (validationWindow.ShowDialog() != true) return;
            }

            var reportsDirectory = Path.Combine(_currentProject.ProjectPath ?? "", "reports");
            if (!string.IsNullOrWhiteSpace(_currentProject.ProjectPath)) Directory.CreateDirectory(reportsDirectory);
            var fileName = SanitizeSuggestedFileName($"{_currentReport.DisplayCode}.docx");
            var dialog = new SaveFileDialog
            {
                Title = "Salvar relatório final",
                Filter = "Documento Word (*.docx)|*.docx",
                AddExtension = true,
                DefaultExt = ".docx",
                FileName = fileName,
                InitialDirectory = Directory.Exists(reportsDirectory) ? reportsDirectory : null,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;

            GeneratedReportText.Text = "Preparando relatório... Processando trechos e inserindo imagens...";
            var path = await _wordReportService.GenerateToAsync(_currentProject, _currentReport, dialog.FileName);
            if (_sharedSession?.IsReadOnly != true) { _hasPendingChanges = true; await SavePendingAsync(); }
            GeneratedReportText.Text = $"✓ Relatório gerado\n{Path.GetFileName(path)}";
            OpenReportButton.IsEnabled = true; OpenReportFolderButton.IsEnabled = true;
            ReportsList.Items.Refresh();
            RefreshOverview();
            RefreshReportReview();
        }
        catch (Exception ex) { GeneratedReportText.Text = "Falha ao gerar relatório."; ShowError(ex.Message, ex); }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentReport?.GeneratedReportPath;
        if (!File.Exists(path)) { MessageBox.Show("O relatório gerado não foi encontrado.", "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
    }

    private void OpenReportFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentReport?.GeneratedReportPath;
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void RefreshReportSectionSelection()
    {
        if (_currentProject is null)
        {
            ReportSectionsList.ItemsSource = null;
            return;
        }
        var selectedIds = _currentReport?.SectionLinks.Select(x => x.SectionId).ToHashSet() ?? [];
        ReportSectionsList.ItemsSource = _currentProject.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name)
            .SelectMany(location => location.Sections.OrderBy(x => x.Order).ThenBy(x => x.Name)
                .Select(section => new ReportSectionSelectionItem(section.Id,
                    $"{location.Name} — {section.Name}", selectedIds.Contains(section.Id))))
            .ToList();
    }

    private void ReportSectionSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingFields || _loadingInspections || _currentProject is null || _currentReport is null || IsReadOnly ||
            sender is not CheckBox { Tag: Guid sectionId } checkBox) return;
        var section = _currentProject.Locations.SelectMany(x => x.Sections).SingleOrDefault(x => x.Id == sectionId);
        if (section is null) return;
        if (checkBox.IsChecked == true) _currentReport.SelectSection(section);
        else _currentReport.SectionLinks.RemoveAll(x => x.SectionId == sectionId);
        NormalizeReportSectionOrder();
        RefreshReportReview();
        ScheduleSave();
    }

    private void SelectAllReportSections_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentReport is null || IsReadOnly) return;
        foreach (var section in _currentProject.Locations.OrderBy(x => x.Order).SelectMany(x => x.Sections.OrderBy(y => y.Order)))
            _currentReport.SelectSection(section);
        NormalizeReportSectionOrder();
        RefreshReportSectionSelection();
        RefreshReportReview();
        ScheduleSave();
    }

    private void ClearReportSections_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null || IsReadOnly) return;
        _currentReport.SectionLinks.Clear();
        RefreshReportSectionSelection();
        RefreshReportReview();
        ScheduleSave();
    }

    private void NormalizeReportSectionOrder()
    {
        if (_currentProject is null || _currentReport is null) return;
        var order = _currentProject.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name)
            .SelectMany(x => x.Sections.OrderBy(y => y.Order).ThenBy(y => y.Name))
            .Select((section, index) => (section.Id, index)).ToDictionary(x => x.Id, x => x.index);
        foreach (var link in _currentReport.SectionLinks)
            link.Order = order.GetValueOrDefault(link.SectionId, int.MaxValue);
    }

    private static string SanitizeSuggestedFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }

    private bool IsReadOnly => _sharedSession?.IsReadOnly == true;

    private void RefreshOverview()
    {
        if (_currentProject is null) return;
        var sections = _currentProject.Locations.SelectMany(x => x.Sections).ToList();
        OverviewProjectName.Text = _currentProject.Name;
        OverviewClientName.Text = string.IsNullOrWhiteSpace(_currentProject.DefaultClient)
            ? "Cliente não informado" : _currentProject.DefaultClient;
        OverviewReportsCount.Text = _currentProject.Reports.Count.ToString(CultureInfo.CurrentCulture);
        OverviewSectionsCount.Text = sections.Count.ToString(CultureInfo.CurrentCulture);
        OverviewOccurrencesCount.Text = sections.Sum(x => x.Occurrences.Count).ToString(CultureInfo.CurrentCulture);
        OverviewUpdatedAt.Text = _currentProject.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        var latestReports = _currentProject.Reports.OrderByDescending(x => x.Order).Take(3)
            .Select(x => new OverviewReportItem(
                x.Id,
                x.DisplayCode,
                $"{(x.ReportDate is null ? "Data não informada" : x.ReportDate.Value.ToString("dd/MM/yyyy"))}  •  {x.SectionLinks.Count} trecho(s)",
                string.IsNullOrWhiteSpace(x.GeneratedReportPath) ? "Em andamento" : "Finalizado",
                string.IsNullOrWhiteSpace(x.GeneratedReportPath) ? "#F2B84B" : "#39D17E"))
            .ToList();
        OverviewLatestReportsList.ItemsSource = latestReports;
        OverviewLatestReportsEmptyState.Visibility = latestReports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var activities = new List<OverviewActivityItem>();
        foreach (var section in sections)
        {
            activities.AddRange(section.Occurrences
                .Where(x => x.CreatedAt != default || x.UpdatedAt != default)
                .Select(x => new OverviewActivityItem(
                    "\uE7BA",
                    "Anomalia registrada",
                    section.Name,
                    (x.UpdatedAt != default ? x.UpdatedAt : x.CreatedAt).ToLocalTime())));
            activities.AddRange(section.VideoReferences
                .Where(x => x.CreatedAt is not null)
                .Select(x => new OverviewActivityItem(
                    "\uE714",
                    "Vídeo adicionado",
                    string.IsNullOrWhiteSpace(x.DisplayName) ? section.Name : x.DisplayName,
                    x.CreatedAt!.Value.ToLocalTime())));
        }
        var latestActivity = activities.OrderByDescending(x => x.Timestamp).Take(4).ToList();
        OverviewActivityList.ItemsSource = latestActivity;
        OverviewActivityEmptyState.Visibility = latestActivity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshReportReview(ReportValidationResult? validation = null)
    {
        if (_currentProject is null || _currentReport is null)
        {
            ReportReviewTitle.Text = "Selecione um relatório";
            ReportReviewSummary.Text = "";
            ReportReviewIssues.Text = "";
            return;
        }

        validation ??= _wordReportService.Validate(_currentProject, _currentReport);
        var emissionReady = !string.IsNullOrWhiteSpace(_currentReport.ReportCode) &&
                            _currentReport.ReportDate is not null && _currentReport.TotalInvestigatedMeters > 0;
        var equipmentCount = Enum.GetValues<EquipmentSelection>()
            .Count(x => x != EquipmentSelection.None && _currentReport.Equipments.HasFlag(x));
        ReportReviewTitle.Text = validation.Errors.Count == 0
            ? validation.Warnings.Count == 0 ? "Relatório pronto" : "Relatório pronto com avisos"
            : "Revisão necessária";
        ReportReviewSummary.Text =
            $"Dados da emissão        {(emissionReady ? "✓" : "• pendente")}\n" +
            $"Equipamentos            {(equipmentCount > 0 ? $"✓ {equipmentCount}" : "• nenhum selecionado")}\n" +
            $"Desenvolvimento         ✓\n" +
            $"Trechos                 ✓ {validation.SelectedSections}\n" +
            $"Anomalias               ✓ {validation.Occurrences}\n" +
            $"Imagens                 ✓ {validation.Evidences + _currentReport.DevelopmentImages.Count}";
        var issues = validation.Errors.Select(x => "• " + x)
            .Concat(validation.Warnings.Select(x => "⚠ " + x)).Take(6).ToList();
        ReportReviewIssues.Text = issues.Count == 0 ? "Nenhuma pendência encontrada." : string.Join("\n", issues);
        ReportReviewIssues.Foreground = (System.Windows.Media.Brush)FindResource(
            validation.Errors.Count == 0 && validation.Warnings.Count == 0 ? "SuccessBrush" : "WarningBrush");
    }

    private void RefreshInspectionTree()
    {
        if (_currentProject is null) return;
        _loadingInspections = true;
        var locationId = _currentLocation?.Id;
        LocationsList.ItemsSource = _currentProject.Locations.OrderBy(x => x.Order).ToList();
        _currentLocation = _currentProject.Locations.FirstOrDefault(x => x.Id == locationId)
                           ?? _currentProject.Locations.OrderBy(x => x.Order).FirstOrDefault();
        LocationsList.SelectedItem = _currentLocation;
        RefreshSections();
        RefreshReportSectionSelection();
        RefreshOverview();
        _loadingInspections = false;
    }

    private void RefreshSections()
    {
        var sectionId = _currentSection?.Id;
        SectionsList.ItemsSource = _currentLocation?.Sections.OrderBy(x => x.Order).ToList() ?? [];
        _currentSection = _currentLocation?.Sections.FirstOrDefault(x => x.Id == sectionId)
                          ?? _currentLocation?.Sections.OrderBy(x => x.Order).FirstOrDefault();
        SectionsList.SelectedItem = _currentSection;
        RefreshVideos();
        RefreshCaptures();
        RefreshOccurrences();
        UpdateInspectionWriteState();
    }

    private void RefreshVideos()
    {
        var videoId = _currentVideo?.Id;
        var items = _currentSection?.VideoReferences.OrderBy(x => x.Order)
            .Select(x => new VideoListItem(x)).ToList() ?? [];
        VideosList.ItemsSource = items;
        var selected = items.FirstOrDefault(x => x.Id == videoId) ?? items.FirstOrDefault();
        VideosList.SelectedItem = selected;
        _currentVideo = selected?.Video;
        if (_currentSection is null)
            ShowVideoState("Crie ou selecione um trecho para iniciar.", false);
        else if (items.Count == 0)
            ShowVideoState("Nenhum vídeo neste trecho", !IsReadOnly);
        else if (selected is null)
            ShowVideoState("Selecione um vídeo para iniciar.", false);
        UpdateInspectionWriteState();
    }

    private void RefreshCaptures()
    {
        if (_currentProject is null || _currentSection is null)
        {
            CapturesList.ItemsSource = null;
            PendingCapturesSummary.Text = "(0)";
            return;
        }
        CapturesList.ItemsSource = _currentProject.Captures
            .Where(x => x.SectionId == _currentSection.Id && x.Status == CaptureStatus.Pending)
            .OrderBy(x => x.Order)
            .Select(x => new CaptureListItem(x, FindVideo(x.VideoReferenceId)?.DisplayName ?? Path.GetFileName(x.VideoFilePath)))
            .ToList();
        PendingCapturesSummary.Text = $"({_currentProject.Captures.Count(x => x.SectionId == _currentSection.Id && x.Status == CaptureStatus.Pending)})";
    }

    private void RefreshOccurrences()
    {
        if (_currentSection is null)
        {
            OccurrencesList.ItemsSource = null;
            if (AnomalySummaryText is not null) AnomalySummaryText.Text = "Selecione um trecho";
            OccurrencesEmptyTitle.Text = "Selecione um trecho";
            OccurrencesEmptyDescription.Text = "As anomalias registradas aparecerão aqui.";
            OccurrencesEmptyState.Visibility = Visibility.Visible;
            return;
        }
        var all = _currentSection.Occurrences
            .OrderBy(x => x.Order).ThenBy(x => x.CreatedAt)
            .Select(x => new OccurrenceListItem(x, !IsReadOnly)).ToList();
        var unfilteredCount = all.Count;
        var incomplete = all.Count(x => !x.IsComplete);
        AnomalySummaryText.Text = $"{all.Count} apontamento(s) • {all.Sum(x => x.EvidenceCount)} imagem(ns) • {incomplete} pendência(s)";
        var query = AnomalyListSearchBox?.Text.Trim() ?? "";
        if (query.Length > 0)
            all = all.Where(x => x.Location.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 x.Anomalies.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 x.PositionSummary.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var status = (AnomalyStatusFilter?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (status == "Complete") all = all.Where(x => x.IsComplete).ToList();
        if (status == "Incomplete") all = all.Where(x => !x.IsComplete).ToList();
        OccurrencesList.ItemsSource = all;
        OccurrencesEmptyTitle.Text = unfilteredCount == 0
            ? "Nenhuma anomalia registrada neste trecho."
            : "Nenhuma anomalia encontrada.";
        OccurrencesEmptyDescription.Text = unfilteredCount == 0
            ? "Durante a inspeção, capture um frame para começar."
            : "Ajuste a pesquisa ou o filtro para ver outros registros.";
        OccurrencesEmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshOverview();
    }

    private void OccurrencesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _occurrenceDragStart = e.GetPosition(OccurrencesList);

    private void OccurrencesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsReadOnly) return;
        var position = e.GetPosition(OccurrencesList);
        if (Math.Abs(position.X - _occurrenceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _occurrenceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var container = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is OccurrenceListItem item)
            DragDrop.DoDragDrop(container, item.Id, DragDropEffects.Move);
    }

    private async void OccurrencesList_Drop(object sender, DragEventArgs e)
    {
        if (_currentSection is null || IsReadOnly || !e.Data.GetDataPresent(typeof(Guid))) return;
        var sourceId = (Guid)e.Data.GetData(typeof(Guid));
        var targetContainer = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is not OccurrenceListItem target || target.Id == sourceId) return;
        var ordered = _currentSection.Occurrences.OrderBy(x => x.Order).ThenBy(x => x.CreatedAt).ToList();
        var source = ordered.FirstOrDefault(x => x.Id == sourceId);
        if (source is null) return;
        ordered.Remove(source);
        var targetIndex = ordered.FindIndex(x => x.Id == target.Id);
        if (targetIndex < 0) return;
        ordered.Insert(targetIndex, source);
        for (var index = 0; index < ordered.Count; index++) ordered[index].Order = index;
        await SaveInspectionChangesAsync();
        RefreshOccurrences();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void AnomalyListFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) RefreshOccurrences();
    }

    private void UpdateInspectionWriteState()
    {
        AddLocationButton.IsEnabled = !IsReadOnly && _currentProject is not null;
        AddSectionButton.IsEnabled = !IsReadOnly && _currentLocation is not null;
        AddVideoButton.IsEnabled = !IsReadOnly && _currentSection is not null;
        AddVideoButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : _currentSection is null ? "Selecione ou crie um trecho antes de adicionar um vídeo."
            : "Adicionar vídeo ao trecho selecionado";
        EmptyVideoButton.IsEnabled = AddVideoButton.IsEnabled;
        CaptureFrameButton.IsEnabled = !IsReadOnly && _currentVideo is not null && File.Exists(_currentVideo.FilePath);
        CaptureFrameOverlayButton.IsEnabled = CaptureFrameButton.IsEnabled;
        RelocateVideoButton.IsEnabled = !IsReadOnly && _currentVideo is not null && !File.Exists(_currentVideo.FilePath);
        RelocateVideoButton.Visibility = _currentVideo is not null && !File.Exists(_currentVideo.FilePath)
            ? Visibility.Visible : Visibility.Collapsed;
        RemoveVideoButton.IsEnabled = !IsReadOnly && _currentVideo is not null;
        NewOccurrenceButton.IsEnabled = !IsReadOnly && _currentSection is not null;
    }

    private void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || IsReadOnly) return;
        NewLocationNameBox.Clear();
        NewLocationValidationText.Visibility = Visibility.Collapsed;
        NewSectionPanel.Visibility = Visibility.Collapsed;
        NewLocationPanel.Visibility = Visibility.Visible;
        HideVideoSurfaceForOverlay();
        InspectionFormOverlay.Visibility = Visibility.Visible;
        NewLocationNameBox.Focus();
    }

    private async void CreateLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || IsReadOnly) return;
        var name = NewLocationNameBox.Text.Trim();
        if (name.Length == 0)
        {
            NewLocationValidationText.Visibility = Visibility.Visible;
            NewLocationNameBox.Focus();
            return;
        }
        var location = new Location { ProjectId = _currentProject.Id, Name = name, Order = _currentProject.Locations.Count };
        _currentProject.Locations.Add(location);
        _currentLocation = location;
        CloseInspectionForm();
        RefreshInspectionTree();
        await SaveInspectionChangesAsync();
    }

    private void AddSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentLocation is null || IsReadOnly) return;
        NewSectionNameBox.Clear(); NewSectionStartBox.Clear(); NewSectionEndBox.Clear();
        NewSectionDiameterBox.Clear(); NewSectionReferenceBox.Clear(); NewSectionDescriptionBox.Clear();
        NewSectionValidationText.Text = "Campos opcionais podem permanecer vazios.";
        NewSectionValidationText.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        NewLocationPanel.Visibility = Visibility.Collapsed;
        NewSectionPanel.Visibility = Visibility.Visible;
        HideVideoSurfaceForOverlay();
        InspectionFormOverlay.Visibility = Visibility.Visible;
        NewSectionNameBox.Focus();
    }

    private async void CreateSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentLocation is null || IsReadOnly) return;
        if (string.IsNullOrWhiteSpace(NewSectionNameBox.Text))
        {
            NewSectionNameBox.Focus();
            return;
        }
        decimal? diameter = null;
        decimal parsed = 0;
        if (!string.IsNullOrWhiteSpace(NewSectionDiameterBox.Text) &&
            !decimal.TryParse(NewSectionDiameterBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
        {
            NewSectionValidationText.Text = "Informe um diâmetro válido ou deixe o campo vazio.";
            NewSectionValidationText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            NewSectionDiameterBox.Focus(); NewSectionDiameterBox.SelectAll();
            return;
        }
        else if (!string.IsNullOrWhiteSpace(NewSectionDiameterBox.Text)) diameter = parsed;
        var section = new InspectionSection
        {
            LocationId = _currentLocation.Id,
            Name = NewSectionNameBox.Text.Trim(),
            Description = EmptyToNull(NewSectionDescriptionBox.Text),
            Diameter = diameter,
            StartPoint = EmptyToNull(NewSectionStartBox.Text),
            EndPoint = EmptyToNull(NewSectionEndBox.Text),
            Reference = EmptyToNull(NewSectionReferenceBox.Text),
            Order = _currentLocation.Sections.Count
        };
        _currentLocation.Sections.Add(section);
        _currentSection = section;
        CloseInspectionForm();
        RefreshSections();
        await SaveInspectionChangesAsync();
    }

    private void CancelInspectionForm_Click(object sender, RoutedEventArgs e) => CloseInspectionForm();

    private void CloseInspectionForm()
    {
        InspectionFormOverlay.Visibility = Visibility.Collapsed;
        NewLocationPanel.Visibility = Visibility.Collapsed;
        NewSectionPanel.Visibility = Visibility.Collapsed;
        RestoreVideoSurfaceAfterOverlay();
    }

    private void LocationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspections) return;
        StopAndReleaseCurrentMedia();
        _currentLocation = LocationsList.SelectedItem as Location;
        _currentSection = null;
        _currentVideo = null;
        _loadingInspections = true;
        RefreshSections();
        _loadingInspections = false;
    }

    private void SectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspections) return;
        StopAndReleaseCurrentMedia();
        _currentSection = SectionsList.SelectedItem as InspectionSection;
        _currentVideo = null;
        _loadingInspections = true;
        RefreshVideos();
        RefreshCaptures();
        RefreshOccurrences();
        _loadingInspections = false;
    }

    private async void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection is null || IsReadOnly) return;
        var dialog = new OpenFileDialog
        {
            Title = "Adicionar vídeo ao trecho",
            Filter = "Vídeos (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var added = new List<VideoReference>();
        foreach (var file in dialog.FileNames)
        {
            if (_currentSection.VideoReferences.Any(x => PathsEqual(x.FilePath, file))) continue;
            var video = new VideoReference
            {
                InspectionSectionId = _currentSection.Id,
                FilePath = file,
                DisplayName = Path.GetFileName(file),
                CreatedAt = DateTime.UtcNow,
                Order = _currentSection.VideoReferences.Count
            };
            _currentSection.VideoReferences.Add(video);
            added.Add(video);
        }
        if (added.Count == 0) return;
        _currentVideo = added[0];
        await SaveInspectionChangesAsync();
        _loadingInspections = true;
        RefreshVideos();
        _loadingInspections = false;
        await LoadVideoAsync(_currentVideo);
    }

    private async void VideosList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspections) return;
        _currentVideo = (VideosList.SelectedItem as VideoListItem)?.Video;
        UpdateInspectionWriteState();
        if (_currentVideo is null) { StopAndReleaseCurrentMedia(); return; }
        await LoadVideoAsync(_currentVideo);
    }

    private async Task LoadVideoAsync(VideoReference video, TimeSpan? seekTo = null)
    {
        StopAndReleaseCurrentMedia();
        _currentVideo = video;
        _playerErrorNotified = false;
        if (!File.Exists(video.FilePath))
        {
            ShowVideoState("Vídeo não encontrado. Use ‘Localizar arquivo’.", false);
            RefreshVideos();
            return;
        }
        try
        {
            ShowVideoState("Carregando vídeo...", false);
            _currentMedia = new Media(_libVlc, video.FilePath, FromType.FromPath);
            if (!_mediaPlayer.Play(_currentMedia)) throw new InvalidOperationException("O player não conseguiu iniciar o vídeo.");
            _logger.Info($"Vídeo aberto: {video.FilePath}");
            if (seekTo is not null)
            {
                await WaitForMediaReadyAsync();
                _mediaPlayer.Time = Math.Max(0, (long)seekTo.Value.TotalMilliseconds);
                await Task.Delay(180);
                _mediaPlayer.SetPause(true);
            }
        }
        catch (Exception ex)
        {
            ShowVideoState("Não foi possível abrir este vídeo.", false);
            _logger.Error($"Falha ao abrir vídeo. Caminho='{video.FilePath}', extensão='{Path.GetExtension(video.FilePath)}', backend='LibVLC'.", ex);
        }
    }

    private async Task WaitForMediaReadyAsync()
    {
        for (var attempt = 0; attempt < 100 && _mediaPlayer.Length <= 0; attempt++) await Task.Delay(50);
        if (_mediaPlayer.Length <= 0) throw new InvalidOperationException("Não foi possível obter a duração do vídeo.");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null) return;
        if (_currentMedia is null) { _ = LoadVideoAsync(_currentVideo); return; }
        if (_mediaPlayer.IsPlaying) _mediaPlayer.SetPause(true);
        else _mediaPlayer.Play();
    }

    private void Back5_Click(object sender, RoutedEventArgs e) => SeekRelative(-5);
    private void Back1_Click(object sender, RoutedEventArgs e) => SeekRelative(-1);
    private void Forward1_Click(object sender, RoutedEventArgs e) => SeekRelative(1);
    private void Forward5_Click(object sender, RoutedEventArgs e) => SeekRelative(5);

    private void SeekRelative(int seconds)
    {
        if (_currentMedia is null) return;
        var maximum = Math.Max(0, _mediaPlayer.Length);
        _mediaPlayer.Time = Math.Clamp(_mediaPlayer.Time + seconds * 1000L, 0, maximum);
    }

    private void ProgressSlider_DragStarted(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void ProgressSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        if (_currentMedia is not null) _mediaPlayer.Time = (long)ProgressSlider.Value;
        _seeking = false;
    }

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && _currentMedia is not null)
            _mediaPlayer.SetRate(rate);
    }

    private async void CaptureFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || _currentVideo is null || IsReadOnly) return;
        try
        {
            _mediaPlayer.SetPause(true);
            await Task.Delay(120);
            _captureDraft = await _frameCaptureService.CaptureCurrentFrameAsync(
                _currentProject, _currentSection, _currentVideo, _mediaPlayer, IsReadOnly);
            await AcceptCaptureDraftAsync(true);
        }
        catch (Exception ex) { ShowError("Não foi possível capturar o frame diretamente do vídeo.", ex); }
    }

    private void RetakeCapture_Click(object sender, RoutedEventArgs e)
    {
        _frameCaptureService.Discard(_captureDraft);
        _captureDraft = null;
        CapturePreviewPanel.Visibility = Visibility.Collapsed;
        CapturePreviewImage.Source = null;
    }

    private async void AcceptCapture_Click(object sender, RoutedEventArgs e) => await AcceptCaptureDraftAsync(false);

    private async void CreateOccurrenceFromCapture_Click(object sender, RoutedEventArgs e) => await AcceptCaptureDraftAsync(true);

    private async Task AcceptCaptureDraftAsync(bool createOccurrence)
    {
        if (_currentProject is null || _captureDraft is null || IsReadOnly) return;
        var capture = _frameCaptureService.Accept(_currentProject, _captureDraft, IsReadOnly);
        _captureDraft = null;
        CapturePreviewPanel.Visibility = Visibility.Collapsed;
        CapturePreviewImage.Source = null;
        RefreshCaptures();
        await SaveInspectionChangesAsync();
        if (createOccurrence) await OpenOccurrenceEditorAsync(null, capture);
    }

    private async void NewOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || IsReadOnly) return;
        Capture? automaticCapture = null;
        try
        {
            if (_currentVideo is not null && _currentMedia is not null && File.Exists(_currentVideo.FilePath))
            {
                await PauseOnDisplayedFrameAsync();
                var draft = await _frameCaptureService.CaptureCurrentFrameAsync(
                    _currentProject, _currentSection, _currentVideo, _mediaPlayer, IsReadOnly);
                automaticCapture = _frameCaptureService.Accept(_currentProject, draft, IsReadOnly);
            }
            await OpenOccurrenceEditorAsync(null, automaticCapture, automaticCapture);
        }
        catch (Exception ex)
        {
            if (automaticCapture is not null && automaticCapture.Status == CaptureStatus.Pending)
                _frameCaptureService.DeletePending(_currentProject, automaticCapture, IsReadOnly);
            ShowError("Não foi possível iniciar o cadastro no frame atual do vídeo.", ex);
        }
    }

    private async void CreateOccurrenceFromPending_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || ((Button)sender).Tag is not Guid id || IsReadOnly) return;
        await OpenOccurrenceEditorAsync(null, _currentProject.Captures.SingleOrDefault(x => x.Id == id));
    }

    private async void EditOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection is null || ((Button)sender).Tag is not Guid id) return;
        await OpenOccurrenceEditorAsync(_currentSection.Occurrences.SingleOrDefault(x => x.Id == id), null);
    }

    private async void DuplicateOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection is null || IsReadOnly || ((Button)sender).Tag is not Guid id) return;
        await OpenOccurrenceEditorAsync(_currentSection.Occurrences.SingleOrDefault(x => x.Id == id), null, true);
    }

    private async Task OpenOccurrenceEditorAsync(Occurrence? occurrence, Capture? initialCapture, bool duplicate = false) =>
        await OpenOccurrenceEditorAsync(occurrence, initialCapture, null, duplicate);

    private async Task OpenOccurrenceEditorAsync(Occurrence? occurrence, Capture? initialCapture,
        Capture? automaticCapture, bool duplicate = false)
    {
        if (_currentProject is null || _currentSection is null || (occurrence is null && IsReadOnly)) return;
        try
        {
            var catalog = await _store.ListAnomalyDefinitionsAsync();
            var editor = new OccurrenceWindow(_currentProject, _currentSection, catalog, _occurrenceService,
                occurrence, initialCapture, IsReadOnly, duplicate) { IsEmbedded = true };
            editor.AutomaticCapture = automaticCapture;
            editor.RecaptureAsync = RecaptureOccurrenceFrameAsync;
            _embeddedOccurrenceEditor = editor;
            editor.EmbeddedCompleted += EmbeddedOccurrenceEditor_Completed;
            var content = (UIElement)editor.Content;
            editor.Content = null;
            OccurrenceEditorHost.Content = content;
            HideVideoSurfaceForOverlay();
            OccurrenceEditorOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (automaticCapture is { Status: CaptureStatus.Pending } && _currentProject is not null &&
                _currentProject.Captures.Contains(automaticCapture))
                _frameCaptureService.DeletePending(_currentProject, automaticCapture, IsReadOnly);
            RestoreVideoSurfaceAfterOverlay();
            _logger.Error("Falha ao salvar anomalia técnica.", ex);
            ShowError("Não foi possível salvar a anomalia.", ex);
        }
    }

    private async void EmbeddedOccurrenceEditor_Completed(object? sender, EventArgs e)
    {
        if (sender is not OccurrenceWindow editor) return;
        editor.EmbeddedCompleted -= EmbeddedOccurrenceEditor_Completed;
        OccurrenceEditorOverlay.Visibility = Visibility.Collapsed;
        OccurrenceEditorHost.Content = null;
        _embeddedOccurrenceEditor = null;
        RestoreVideoSurfaceAfterOverlay();
        try
        {
            if (editor.RequestedVideoReferenceId is Guid videoId && editor.RequestedVideoTimestamp is not null)
            {
                await NavigateToVideoAsync(videoId, editor.RequestedVideoTimestamp.Value);
                return;
            }
            if (editor.EmbeddedResult != true || editor.SavedDraft is null || _currentProject is null || _currentSection is null)
            {
                if (editor.EmbeddedResult is null && editor.AutomaticCapture is { Status: CaptureStatus.Pending } automatic &&
                    _currentProject is not null && _currentProject.Captures.Contains(automatic))
                    _frameCaptureService.DeletePending(_currentProject, automatic, IsReadOnly);
                RefreshCaptures();
                if (editor.EmbeddedResult == false) await SaveInspectionChangesAsync();
                return;
            }
            await _occurrenceService.SaveAsync(_currentProject, _currentSection, editor.SavedDraft, IsReadOnly);
            RefreshCaptures();
            RefreshOccurrences();
            await SaveInspectionChangesAsync();
            if (editor.ResumeVideoAfterSave && _currentMedia is not null) _mediaPlayer.Play();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao salvar anomalia técnica.", ex);
            ShowError("Não foi possível salvar a anomalia.", ex);
        }
    }

    private async Task<Capture?> RecaptureOccurrenceFrameAsync(Capture currentCapture, TimeSpan timestamp)
    {
        if (_currentProject is null || _currentSection is null || _currentVideo is null || IsReadOnly) return null;
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Length));
        if (duration > TimeSpan.Zero && timestamp > duration) timestamp = duration;
        if (timestamp < TimeSpan.Zero) timestamp = TimeSpan.Zero;
        _mediaPlayer.Time = (long)timestamp.TotalMilliseconds;
        await WaitForDisplayedFrameAsync(timestamp);
        var draft = await _frameCaptureService.CaptureCurrentFrameAsync(
            _currentProject, _currentSection, _currentVideo, _mediaPlayer, IsReadOnly);
        if (Math.Abs((draft.VideoTimestamp - timestamp).TotalMilliseconds) > 100)
        {
            _frameCaptureService.Discard(draft);
            throw new InvalidOperationException("O player não estabilizou no timestamp solicitado para refazer o frame.");
        }
        var replacement = _frameCaptureService.Accept(_currentProject, draft, IsReadOnly);
        if (currentCapture.Status == CaptureStatus.Pending && _currentProject.Captures.Contains(currentCapture))
            _frameCaptureService.DeletePending(_currentProject, currentCapture, IsReadOnly);
        return replacement;
    }

    private async Task PauseOnDisplayedFrameAsync()
    {
        _mediaPlayer.SetPause(true);
        var previous = _mediaPlayer.Time;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(35);
            var current = _mediaPlayer.Time;
            if (Math.Abs(current - previous) <= 2) break;
            previous = current;
        }
        await Task.Delay(45);
    }

    private void HideVideoSurfaceForOverlay()
    {
        if (_currentMedia is not null) _mediaPlayer.SetPause(true);
        VideoView.Visibility = Visibility.Collapsed;
        VideoView.MediaPlayer = null;
    }

    private void RestoreVideoSurfaceAfterOverlay()
    {
        if (_playerDisposed) return;
        VideoView.MediaPlayer = _mediaPlayer;
        VideoView.Visibility = Visibility.Visible;
    }

    private async Task WaitForDisplayedFrameAsync(TimeSpan target)
    {
        _mediaPlayer.SetPause(true);
        var targetMilliseconds = Math.Max(0, (long)target.TotalMilliseconds);
        var reachedTarget = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (Math.Abs(_mediaPlayer.Time - targetMilliseconds) <= 80) { reachedTarget = true; break; }
            await Task.Delay(25);
        }
        if (!reachedTarget)
            throw new InvalidOperationException("O player não alcançou o timestamp solicitado.");
        await Task.Delay(120);
    }

    private async void DeleteOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || ((Button)sender).Tag is not Guid id || IsReadOnly) return;
        var occurrence = _currentSection.Occurrences.SingleOrDefault(x => x.Id == id);
        if (occurrence is null || MessageBox.Show("Excluir esta anomalia? As capturas vinculadas voltarão para Pendentes.",
                "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _occurrenceService.Delete(_currentProject, _currentSection, occurrence, IsReadOnly);
        RefreshCaptures();
        RefreshOccurrences();
        await SaveInspectionChangesAsync();
    }

    private async void GoToOccurrenceVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || ((Button)sender).Tag is not Guid id) return;
        var occurrence = _currentSection.Occurrences.SingleOrDefault(x => x.Id == id);
        var evidence = occurrence?.Evidence.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Order)
            .FirstOrDefault(x => x.VideoReferenceId is not null && x.VideoTimestamp is not null);
        if (evidence?.VideoReferenceId is not Guid videoId || evidence.VideoTimestamp is null) return;
        await NavigateToVideoAsync(videoId, evidence.VideoTimestamp.Value);
    }

    private async Task NavigateToVideoAsync(Guid videoId, TimeSpan timestamp)
    {
        if (_currentProject is null || _currentSection is null) return;
        var video = FindVideo(videoId);
        var location = _currentProject.Locations.FirstOrDefault(x => x.Sections.Any(y => y.Id == _currentSection.Id));
        if (video is null || location is null) return;
        _loadingInspections = true;
        _currentLocation = location; _currentVideo = video;
        LocationsList.SelectedItem = location;
        SectionsList.ItemsSource = location.Sections.OrderBy(x => x.Order).ToList();
        SectionsList.SelectedItem = _currentSection;
        RefreshVideos();
        VideosList.SelectedItem = ((IEnumerable<VideoListItem>)VideosList.ItemsSource).FirstOrDefault(x => x.Id == video.Id);
        _loadingInspections = false;
        await LoadVideoAsync(video, timestamp);
    }

    private async void DeleteCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || ((Button)sender).Tag is not Guid id || IsReadOnly) return;
        var capture = _currentProject.Captures.SingleOrDefault(x => x.Id == id);
        if (capture is null || MessageBox.Show("Excluir esta captura pendente e seu arquivo PNG?", "Confirmar exclusão",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _frameCaptureService.DeletePending(_currentProject, capture, IsReadOnly);
        RefreshCaptures();
        await SaveInspectionChangesAsync();
    }

    private async void GoToCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || ((Button)sender).Tag is not Guid id) return;
        var capture = _currentProject.Captures.SingleOrDefault(x => x.Id == id);
        if (capture is null) return;
        var section = FindSection(capture.SectionId);
        var location = _currentProject.Locations.FirstOrDefault(x => x.Sections.Any(y => y.Id == capture.SectionId));
        var video = FindVideo(capture.VideoReferenceId);
        if (section is null || location is null || video is null) return;
        _loadingInspections = true;
        _currentLocation = location; _currentSection = section; _currentVideo = video;
        LocationsList.SelectedItem = location;
        SectionsList.ItemsSource = location.Sections.OrderBy(x => x.Order).ToList();
        SectionsList.SelectedItem = section;
        RefreshVideos();
        VideosList.SelectedItem = ((IEnumerable<VideoListItem>)VideosList.ItemsSource).FirstOrDefault(x => x.Id == video.Id);
        RefreshCaptures();
        _loadingInspections = false;
        await LoadVideoAsync(video, capture.VideoTimestamp);
    }

    private async void RelocateVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null || IsReadOnly) return;
        var id = _currentVideo.Id;
        var dialog = new OpenFileDialog
        {
            Title = "Localizar o vídeo",
            Filter = "Vídeos (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        FrameCaptureService.RelocateVideo(_currentVideo, dialog.FileName, IsReadOnly);
        if (_currentVideo.Id != id) throw new InvalidOperationException("A relocalização alterou indevidamente a identidade do vídeo.");
        RefreshVideos();
        await SaveInspectionChangesAsync();
        await LoadVideoAsync(_currentVideo);
    }

    private async void RemoveVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || _currentVideo is null || IsReadOnly) return;
        if (_currentProject.Captures.Any(x => x.VideoReferenceId == _currentVideo.Id))
        {
            MessageBox.Show("Este vídeo possui capturas e sua referência deve ser preservada.", "GeoAutoDocs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show("Remover somente a referência deste vídeo? O arquivo original não será alterado.",
                "GeoAutoDocs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopAndReleaseCurrentMedia();
        _currentSection.VideoReferences.Remove(_currentVideo);
        _currentVideo = null;
        RefreshVideos();
        await SaveInspectionChangesAsync();
    }

    private async Task SaveInspectionChangesAsync()
    {
        if (_currentProject is null || IsReadOnly) return;
        _hasPendingChanges = true;
        await SavePendingAsync();
        if (_sharedSession is not null) _sharedDirty = true;
    }

    private void RefreshPlayerPosition()
    {
        if (_playerDisposed) return;
        var current = Math.Max(0, _mediaPlayer.Time);
        var duration = Math.Max(0, _mediaPlayer.Length);
        if (!_seeking)
        {
            ProgressSlider.Maximum = Math.Max(1, duration);
            ProgressSlider.Value = Math.Min(current, ProgressSlider.Maximum);
        }
        TimeText.Text = $"{FormatTime(TimeSpan.FromMilliseconds(current))} / {FormatTime(TimeSpan.FromMilliseconds(duration))}";
    }

    private void HandlePlayerError()
    {
        if (_playerErrorNotified) return;
        _playerErrorNotified = true;
        ShowVideoState("Não foi possível abrir este vídeo.", false);
        _logger.Error($"Erro de codec, rede ou leitura. Caminho='{_currentVideo?.FilePath}', extensão='{Path.GetExtension(_currentVideo?.FilePath)}', backend='LibVLC'.",
            new IOException("O LibVLC informou falha durante a reprodução."));
    }

    private void StopAndReleaseCurrentMedia()
    {
        if (_playerDisposed) return;
        try { _mediaPlayer.Stop(); } catch (Exception ex) { _logger.Error("Falha ao interromper o player.", ex); }
        _currentMedia?.Dispose();
        _currentMedia = null;
        if (_currentSection is null)
            ShowVideoState("Crie ou selecione um trecho para iniciar.", false);
        else if (_currentSection.VideoReferences.Count == 0)
            ShowVideoState("Nenhum vídeo neste trecho", !IsReadOnly);
        else
            ShowVideoState("Selecione um vídeo para iniciar.", false);
        PlayPauseButton.Content = "▶";
    }

    private void ShowVideoState(string message, bool showAddVideo)
    {
        VideoLoadedBadge.Visibility = Visibility.Collapsed;
        VideoMessage.Text = message;
        EmptyVideoButton.Visibility = showAddVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoEmptyState.Visibility = Visibility.Visible;
    }

    private void DisposePlayer()
    {
        if (_playerDisposed) return;
        _playerDisposed = true;
        _playerTimer.Stop();
        try { _mediaPlayer.Stop(); } catch { }
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OccurrenceEditorOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) _embeddedOccurrenceEditor?.CancelEmbedded();
            else return;
            e.Handled = true;
            return;
        }

        if (ProjectDialogOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) CloseProjectDialog();
            else if (e.Key == Key.Enter && RenameProjectDialogPanel.Visibility == Visibility.Visible)
                ConfirmRenameProject_Click(sender, e);
            else if (e.Key == Key.Enter && DeleteProjectDialogPanel.Visibility == Visibility.Visible)
                ConfirmDeleteProject_Click(sender, e);
            else return;
            e.Handled = true;
            return;
        }

        if (NewProjectView.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) CancelNewProject_Click(sender, e);
            else if (e.Key == Key.Enter) CreateProject_Click(sender, e);
            else return;
            e.Handled = true;
            return;
        }

        if (InspectionFormOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) CloseInspectionForm();
            else if (e.Key == Key.Enter && NewLocationPanel.Visibility == Visibility.Visible)
                CreateLocation_Click(sender, e);
            else if (e.Key == Key.Enter && NewSectionPanel.Visibility == Visibility.Visible)
                CreateSection_Click(sender, e);
            else return;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && CapturePreviewPanel.Visibility == Visibility.Visible)
        {
            RetakeCapture_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && InspectionsTab.IsSelected && CapturePreviewPanel.Visibility != Visibility.Visible &&
            Keyboard.FocusedElement is not TextBoxBase and not PasswordBox and not ComboBox { IsEditable: true })
        {
            PlayPause_Click(sender, e);
            e.Handled = true;
        }
    }

    private InspectionSection? FindSection(Guid id) => _currentProject?.Locations
        .SelectMany(x => x.Sections).FirstOrDefault(x => x.Id == id);

    private VideoReference? FindVideo(Guid id) => _currentProject?.Locations
        .SelectMany(x => x.Sections).SelectMany(x => x.VideoReferences).FirstOrDefault(x => x.Id == id);

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string FormatTime(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private static BitmapImage? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapImage? LoadThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 220; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private sealed class VideoListItem
    {
        private readonly VideoReference _video;
        public VideoListItem(VideoReference video) => _video = video;
        public VideoReference Video => _video;
        public Guid Id => _video.Id;
        public string DisplayName => string.IsNullOrWhiteSpace(_video.DisplayName) ? Path.GetFileName(_video.FilePath) : _video.DisplayName;
        public string Availability => File.Exists(_video.FilePath) ? "Disponível" : "Vídeo não encontrado";
        public string StatusColor => File.Exists(_video.FilePath) ? "#39D17E" : "#EC6670";
        public string PathKind => _video.Status == MediaReferenceStatus.SharedUnc ? "Arquivo em rede (UNC)" : "Arquivo local";
    }

    private sealed class CaptureListItem(Capture capture, string? videoName)
    {
        public Guid Id => capture.Id;
        public BitmapImage? Thumbnail { get; } = LoadThumbnail(capture.FilePath);
        public string Timestamp => FormatTime(capture.VideoTimestamp);
        public string VideoName => string.IsNullOrWhiteSpace(videoName) ? "Vídeo de origem" : videoName;
    }

    private sealed class DevelopmentImagePreviewItem(string filePath)
    {
        public BitmapImage? Thumbnail { get; } = LoadThumbnail(filePath);
        public string DisplayName { get; } = Path.GetFileName(filePath);
    }

    private sealed class OccurrenceListItem
    {
        public Guid Id { get; }
        public BitmapImage? Thumbnail { get; }
        public string Location { get; }
        public string Anomalies { get; }
        public string EvidenceSummary { get; }
        public int EvidenceCount { get; }
        public string PositionSummary { get; }
        public string TechnicalSummary { get; }
        public string Completeness { get; }
        public string CompletenessColor { get; }
        public bool CanGoToVideo { get; }
        public bool CanModify { get; }
        public bool IsComplete { get; }

        public OccurrenceListItem(Occurrence occurrence, bool canModify)
        {
            Id = occurrence.Id;
            CanModify = canModify;
            Location = OccurrenceRules.FormatLocation(occurrence);
            Anomalies = string.Join(" + ", occurrence.Anomalies.OrderBy(x => x.Order).Select(x =>
                x.AnomalyType.AllowsCustomText && !string.IsNullOrWhiteSpace(x.CustomText)
                    ? $"Outro: {x.CustomText}" : x.AnomalyType.Name));
            EvidenceCount = occurrence.Evidence.Count;
            EvidenceSummary = $"{EvidenceCount} imagem(ns)";
            PositionSummary = string.Join(" • ", occurrence.Anomalies.Select(x => OccurrenceRules.FormatPosition(x.Position))
                .Where(x => x != "Não informado").Distinct());
            if (PositionSummary.Length == 0) PositionSummary = "Posição não informada";
            TechnicalSummary = string.Join(" • ", occurrence.Anomalies.OrderBy(x => x.Order).Select(x =>
            {
                var values = new List<string>();
                if (x.Orientation != AnomalyOrientation.NotReported) values.Add(OccurrenceRules.FormatOrientation(x.Orientation));
                if (x.ApproximateExtension is not null) values.Add($"ext. {x.ApproximateExtension:N2} m");
                if (!string.IsNullOrWhiteSpace(x.Dimension)) values.Add(x.Dimension);
                if (x.EstimatedPercentage is not null) values.Add($"{x.EstimatedPercentage:N2}%");
                return values.Count == 0 ? "" : $"{x.AnomalyType.Name}: {string.Join(", ", values)}";
            }).Where(x => x.Length > 0));
            var first = occurrence.Evidence.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Order)
                .FirstOrDefault(x => File.Exists(x.FilePath));
            Thumbnail = LoadThumbnail(first?.FilePath);
            CanGoToVideo = occurrence.Evidence.Any(x => x.VideoReferenceId is not null && x.VideoTimestamp is not null);
            IsComplete = OccurrenceRules.IsComplete(occurrence);
            var pending = OccurrenceRules.GetPendingReasons(occurrence);
            Completeness = IsComplete ? "✓ Completa" : "⚠ " + string.Join(" • ", pending);
            CompletenessColor = IsComplete ? "#39D17E" : "#F2B84B";
        }
    }

    private sealed record ReportSectionSelectionItem(Guid SectionId, string DisplayName, bool IsSelected);

    private sealed record OverviewReportItem(Guid Id, string Title, string Detail, string Status, string StatusColor);

    private sealed class OverviewActivityItem(string icon, string title, string detail, DateTime timestamp)
    {
        public string Icon { get; } = icon;
        public string Title { get; } = title;
        public string Detail { get; } = detail;
        public DateTime Timestamp { get; } = timestamp;
        public string When { get; } = timestamp.ToString("dd/MM/yyyy HH:mm");
    }

    private void ScheduleSave()
    {
        if (_loadingFields || _currentProject is null || _sharedSession?.IsReadOnly == true) return;
        _hasPendingChanges = true; SaveStatus.Text = "Salvando..."; _saveTimer.Stop(); _saveTimer.Start();
    }

    private void ShowError(string message, Exception ex)
    {
        _logger.Error(message, ex); MessageBox.Show(message, "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
