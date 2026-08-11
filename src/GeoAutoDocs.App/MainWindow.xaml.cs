using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow(IProjectStore store, WordReportService wordReportService,
        SharedProjectService sharedProjects, LocalLogger logger, FrameCaptureService frameCaptureService)
    {
        InitializeComponent();
        _store = store;
        _wordReportService = wordReportService;
        _sharedProjects = sharedProjects;
        _logger = logger;
        _frameCaptureService = frameCaptureService;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => HandlePlayerError());
        _mediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() => { VideoMessage.Visibility = Visibility.Collapsed; PlayPauseButton.Content = "❚❚"; });
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
        if (project is null || MessageBox.Show($"Excluir o projeto '{project.Name}'?", "Confirmar exclusão",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _store.DeleteAsync(id); await ReloadProjectsAsync(); }
        catch (Exception ex) { ShowError("Não foi possível excluir o projeto.", ex); }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!await FinalizeCurrentProjectAsync()) return;
        StopAndReleaseCurrentMedia();
        _currentProject = null; _currentReport = null; _sharedSession = null;
        EditorView.Visibility = Visibility.Collapsed;
        ProjectsView.Visibility = Visibility.Visible;
        await ReloadProjectsAsync();
    }

    private void LoadProject(Project project)
    {
        _loadingFields = true;
        ProjectTitle.Text = project.Name;
        DefaultClientBox.Text = project.DefaultClient; DefaultCnpjBox.Text = project.DefaultCnpj;
        DefaultAreaBox.Text = project.DefaultArea; DefaultContractBox.Text = project.DefaultContract;
        DefaultPurchaseOrderBox.Text = project.DefaultPurchaseOrder; DefaultLogoBox.Text = project.DefaultLogoPath ?? "";
        ReportsCombo.ItemsSource = project.Reports.OrderBy(x => x.Order).ToList();
        _currentReport = project.Reports.OrderBy(x => x.Order).FirstOrDefault();
        ReportsCombo.SelectedItem = _currentReport;
        StorageStatus.Text = project.StorageKind == ProjectStorageKind.Shared
            ? (_sharedSession?.IsReadOnly == true ? "Compartilhado • somente leitura" : "Compartilhado • editor com lock")
            : "Local";
        ShareProjectButton.IsEnabled = project.StorageKind == ProjectStorageKind.Local;
        NewReportButton.IsEnabled = _sharedSession?.IsReadOnly != true;
        ProjectDefaultsTab.IsEnabled = !IsReadOnly;
        ReportHeaderTab.IsEnabled = !IsReadOnly && _currentReport is not null;
        WordConfigTab.IsEnabled = !IsReadOnly && _currentReport is not null;
        ReadOnlyInspectionText.Visibility = IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        RefreshInspectionTree();
        UpdateInspectionWriteState();
        LoadReport(_currentReport);
        SaveStatus.Text = $"Salvo automaticamente • {project.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm}";
        _hasPendingChanges = false;
        _loadingFields = false;
    }

    private void LoadReport(Report? report)
    {
        _loadingFields = true;
        var enabled = report is not null;
        ReportHeaderTab.IsEnabled = enabled && !IsReadOnly;
        WordConfigTab.IsEnabled = enabled && !IsReadOnly;
        ClientBox.Text = report?.Client ?? ""; CnpjBox.Text = report?.Cnpj ?? "";
        AreaBox.Text = report?.Area ?? ""; ContractBox.Text = report?.Contract ?? "";
        PurchaseOrderBox.Text = report?.PurchaseOrder ?? ""; ReportCodeBox.Text = report?.ReportCode ?? "";
        ReportDatePicker.SelectedDate = report?.ReportDate; LocationBox.Text = report?.LocationDescription ?? "";
        MetersBox.Text = report?.TotalInvestigatedMeters?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
        TemplateBox.Text = report?.TemplatePath ?? ""; LogoBox.Text = report?.LogoPath ?? "";
        LocationImageBox.Text = report?.LocationImagePath ?? "";
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
        OpeningCheck.IsChecked = report?.IncludeInspectionOpening == true; GprCheck.IsChecked = report?.IncludeGpr == true;
        CustomSectionsList.ItemsSource = report?.CustomSections;
        GeneratedReportText.Text = report?.GeneratedReportPath ?? "";
        OpenReportButton.IsEnabled = File.Exists(report?.GeneratedReportPath);
        _loadingFields = false;
    }

    private void ReportsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFields || _currentProject is null) return;
        ApplyFieldsToModel();
        _currentReport = ReportsCombo.SelectedItem as Report;
        LoadReport(_currentReport);
    }

    private async void NewReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _sharedSession?.IsReadOnly == true) return;
        ApplyFieldsToModel();
        var created = _currentProject.CreateReport();
        created.TemplatePath = _wordReportService.DefaultTemplatePath;
        _loadingFields = true;
        ReportsCombo.ItemsSource = _currentProject.Reports.OrderBy(x => x.Order).ToList();
        _currentReport = created;
        ReportsCombo.SelectedItem = created;
        _loadingFields = false;
        LoadReport(_currentReport);
        _hasPendingChanges = true;
        await SavePendingAsync();
    }

    private void FieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingFields || _currentProject is null || _sharedSession?.IsReadOnly == true) return;
        _hasPendingChanges = true;
        SaveStatus.Text = "Alterações pendentes...";
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void ApplyFieldsToModel()
    {
        if (_currentProject is null) return;
        _currentProject.DefaultClient = DefaultClientBox.Text.Trim(); _currentProject.DefaultCnpj = DefaultCnpjBox.Text.Trim();
        _currentProject.DefaultArea = DefaultAreaBox.Text.Trim(); _currentProject.DefaultContract = DefaultContractBox.Text.Trim();
        _currentProject.DefaultPurchaseOrder = DefaultPurchaseOrderBox.Text.Trim(); _currentProject.DefaultLogoPath = EmptyToNull(DefaultLogoBox.Text);
        if (_currentReport is null) return;
        var report = _currentReport;
        report.Client = ClientBox.Text.Trim(); report.Cnpj = CnpjBox.Text.Trim(); report.Area = AreaBox.Text.Trim();
        report.Contract = ContractBox.Text.Trim(); report.PurchaseOrder = PurchaseOrderBox.Text.Trim();
        report.ReportCode = ReportCodeBox.Text.Trim(); report.ReportDate = ReportDatePicker.SelectedDate;
        report.LocationDescription = LocationBox.Text.Trim();
        report.TotalInvestigatedMeters = decimal.TryParse(MetersBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var meters) ? meters : null;
        report.TemplatePath = EmptyToNull(TemplateBox.Text); report.LogoPath = EmptyToNull(LogoBox.Text); report.LocationImagePath = EmptyToNull(LocationImageBox.Text);
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
            SaveStatus.Text = $"Salvo localmente • {_currentProject.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
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

    private void SelectFile(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        target.Text = dialog.FileName; ScheduleSave();
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
        await SavePendingAsync();
        try
        {
            GeneratedReportText.Text = "Gerando relatório...";
            var path = await _wordReportService.GenerateAsync(_currentProject, _currentReport, includeSection4TechnicalProof: true);
            if (_sharedSession?.IsReadOnly != true) { _hasPendingChanges = true; await SavePendingAsync(); }
            GeneratedReportText.Text = path; OpenReportButton.IsEnabled = true;
            MessageBox.Show("Relatório gerado com sucesso.", "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { GeneratedReportText.Text = "Falha ao gerar relatório."; ShowError(ex.Message, ex); }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentReport?.GeneratedReportPath;
        if (!File.Exists(path)) { MessageBox.Show("O relatório gerado não foi encontrado.", "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
    }

    private bool IsReadOnly => _sharedSession?.IsReadOnly == true;

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
        UpdateInspectionWriteState();
    }

    private void RefreshVideos()
    {
        var videoId = _currentVideo?.Id;
        var items = _currentSection?.VideoReferences.OrderBy(x => x.Order)
            .Select(x => new VideoListItem(x)).ToList() ?? [];
        VideosList.ItemsSource = items;
        var selected = items.FirstOrDefault(x => x.Id == videoId);
        VideosList.SelectedItem = selected;
        if (selected is null) _currentVideo = null;
        UpdateInspectionWriteState();
    }

    private void RefreshCaptures()
    {
        if (_currentProject is null || _currentSection is null)
        {
            CapturesList.ItemsSource = null;
            return;
        }
        CapturesList.ItemsSource = _currentProject.Captures
            .Where(x => x.SectionId == _currentSection.Id && x.Status == CaptureStatus.Pending)
            .OrderBy(x => x.Order)
            .Select(x => new CaptureListItem(x, FindVideo(x.VideoReferenceId)?.DisplayName ?? Path.GetFileName(x.VideoFilePath)))
            .ToList();
    }

    private void UpdateInspectionWriteState()
    {
        AddLocationButton.IsEnabled = !IsReadOnly && _currentProject is not null;
        AddSectionButton.IsEnabled = !IsReadOnly && _currentLocation is not null;
        AddVideoButton.IsEnabled = !IsReadOnly && _currentSection is not null;
        CaptureFrameButton.IsEnabled = !IsReadOnly && _currentVideo is not null && File.Exists(_currentVideo.FilePath);
        RelocateVideoButton.IsEnabled = !IsReadOnly && _currentVideo is not null && !File.Exists(_currentVideo.FilePath);
        RemoveVideoButton.IsEnabled = !IsReadOnly && _currentVideo is not null;
    }

    private async void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || IsReadOnly) return;
        var name = TextPromptWindow.Ask(this, "Novo local", "Nome do local:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var location = new Location { ProjectId = _currentProject.Id, Name = name.Trim(), Order = _currentProject.Locations.Count };
        _currentProject.Locations.Add(location);
        _currentLocation = location;
        RefreshInspectionTree();
        await SaveInspectionChangesAsync();
    }

    private async void AddSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentLocation is null || IsReadOnly) return;
        var name = TextPromptWindow.Ask(this, "Novo trecho", "Nome ou número do trecho:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var diameterText = TextPromptWindow.Ask(this, "Novo trecho", "Diâmetro em mm (opcional):");
        var reference = TextPromptWindow.Ask(this, "Novo trecho", "Referência (opcional):");
        var section = new InspectionSection
        {
            LocationId = _currentLocation.Id,
            Name = name.Trim(),
            Diameter = decimal.TryParse(diameterText, NumberStyles.Number, CultureInfo.CurrentCulture, out var diameter) ? diameter : null,
            Reference = EmptyToNull(reference ?? ""),
            Order = _currentLocation.Sections.Count
        };
        _currentLocation.Sections.Add(section);
        _currentSection = section;
        RefreshSections();
        await SaveInspectionChangesAsync();
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
        foreach (var file in dialog.FileNames)
        {
            if (_currentSection.VideoReferences.Any(x => PathsEqual(x.FilePath, file))) continue;
            _currentSection.VideoReferences.Add(new VideoReference
            {
                InspectionSectionId = _currentSection.Id,
                FilePath = file,
                DisplayName = Path.GetFileName(file),
                CreatedAt = DateTime.UtcNow,
                Order = _currentSection.VideoReferences.Count
            });
        }
        RefreshVideos();
        await SaveInspectionChangesAsync();
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
            VideoMessage.Text = "Vídeo não encontrado. Use ‘Localizar arquivo’.";
            VideoMessage.Visibility = Visibility.Visible;
            RefreshVideos();
            return;
        }
        try
        {
            VideoMessage.Text = "Carregando vídeo...";
            VideoMessage.Visibility = Visibility.Visible;
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
            VideoMessage.Text = "Vídeo indisponível.";
            VideoMessage.Visibility = Visibility.Visible;
            _logger.Error($"Falha ao abrir vídeo: {video.FilePath}", ex);
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
            CapturePreviewImage.Source = LoadBitmap(_captureDraft.FilePath);
            CapturePreviewTimestamp.Text = FormatTime(_captureDraft.VideoTimestamp);
            CapturePreviewPanel.Visibility = Visibility.Visible;
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
        _frameCaptureService.Accept(_currentProject, _captureDraft, IsReadOnly);
        _captureDraft = null;
        CapturePreviewPanel.Visibility = Visibility.Collapsed;
        CapturePreviewImage.Source = null;
        RefreshCaptures();
        await SaveInspectionChangesAsync();
        if (createOccurrence)
            MessageBox.Show("Captura preservada como pendente. O formulário técnico de ocorrência será conectado na Etapa 4.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Information);
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
        VideoMessage.Text = "Vídeo indisponível.";
        VideoMessage.Visibility = Visibility.Visible;
        _logger.Error($"Erro de codec, rede ou leitura no vídeo '{_currentVideo?.FilePath}'.",
            new IOException("O LibVLC informou falha durante a reprodução."));
    }

    private void StopAndReleaseCurrentMedia()
    {
        if (_playerDisposed) return;
        try { _mediaPlayer.Stop(); } catch (Exception ex) { _logger.Error("Falha ao interromper o player.", ex); }
        _currentMedia?.Dispose();
        _currentMedia = null;
        VideoMessage.Text = _currentSection is null ? "Selecione um trecho e um vídeo." : "Selecione um vídeo.";
        VideoMessage.Visibility = Visibility.Visible;
        PlayPauseButton.Content = "▶";
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
        if (e.Key == Key.Escape && CapturePreviewPanel.Visibility == Visibility.Visible)
        {
            RetakeCapture_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && InspectionsTab.IsSelected && CapturePreviewPanel.Visibility != Visibility.Visible)
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

    private sealed class VideoListItem
    {
        private readonly VideoReference _video;
        public VideoListItem(VideoReference video) => _video = video;
        public VideoReference Video => _video;
        public Guid Id => _video.Id;
        public string DisplayName => string.IsNullOrWhiteSpace(_video.DisplayName) ? Path.GetFileName(_video.FilePath) : _video.DisplayName;
        public string Availability => File.Exists(_video.FilePath) ? "Disponível" : "Vídeo não encontrado";
        public string StatusColor => File.Exists(_video.FilePath) ? "#4C7161" : "#9C2731";
        public string PathKind => _video.Status == MediaReferenceStatus.SharedUnc ? "Arquivo em rede (UNC)" : "Arquivo local";
    }

    private sealed class CaptureListItem(Capture capture, string? videoName)
    {
        public Guid Id => capture.Id;
        public BitmapImage? Thumbnail { get; } = LoadBitmap(capture.FilePath);
        public string Timestamp => FormatTime(capture.VideoTimestamp);
        public string VideoName => string.IsNullOrWhiteSpace(videoName) ? "Vídeo de origem" : videoName;
    }

    private void ScheduleSave()
    {
        if (_loadingFields || _currentProject is null || _sharedSession?.IsReadOnly == true) return;
        _hasPendingChanges = true; SaveStatus.Text = "Alterações pendentes..."; _saveTimer.Stop(); _saveTimer.Start();
    }

    private void ShowError(string message, Exception ex)
    {
        _logger.Error(message, ex); MessageBox.Show(message, "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
