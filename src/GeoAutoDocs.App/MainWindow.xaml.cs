using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace GeoAutoDocs.App;

public partial class MainWindow : Window
{
    private enum VideoSurfaceState { Empty, Loading, Missing, Error }

    private readonly IProjectStore _store;
    private readonly WordReportService _wordReportService;
    private readonly SharedProjectService _sharedProjects;
    private readonly LocalLogger _logger;
    private readonly FrameCaptureService _frameCaptureService;
    private readonly OccurrenceService _occurrenceService;
    private readonly IAnomalyCatalogService _anomalyCatalogService;
    private readonly InspectionExchangeService _inspectionExchangeService;
    private readonly IInspectionTransport _geoAutoEngineTransport;
    private readonly IInspectionCorrectionChannel? _correctionChannel;
    private readonly InspectionCorrectionService _correctionService = new();
    private readonly SemaphoreSlim _correctionReviewGate = new(1, 1);
    private BackupStatus _backupStatus;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _saveTimer;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _playerTimer;
    private readonly DispatcherTimer _overviewRefreshTimer;
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
    private bool _refreshingReportSectionSelection;
    private bool _reportSectionBulkUpdateInProgress;
    private readonly ObservableCollection<ReportSectionSelectionItem> _reportSectionItems = [];
    private bool _hasPendingChanges;
    private long _pendingSaveRevision;
    private bool _sharedDirty;
    private bool _checkpointInProgress;
    private bool _finalizationInProgress;
    private bool _closingAfterSave;
    private bool _exitRequested;
    private bool _hideToTrayInProgress;
    private bool _closeToTrayEnabled = true;
    private bool _trayNoticeShown;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _trayPendingCapturesItem;
    private WinForms.ToolStripMenuItem? _closeToTrayMenuItem;
    private Drawing.Icon? _trayIconImage;
    private bool _saveErrorNotified;
    private bool _reportDeletionInProgress;
    private bool _seeking;
    private bool _playerOperationInProgress;
    private bool _videoLoadInProgress;
    private bool _updatingSpeedSelection;
    private float _selectedPlaybackRate = 1f;
    private long _lastKnownPlayerTime;
    private readonly SemaphoreSlim _playerLifecycleGate = new(1, 1);
    private readonly BoundedAsyncExecutor _thumbnailLoadExecutor = new(3);
    private readonly object _thumbnailCacheSync = new();
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailCacheOrder = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _thumbnailFailures = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _inspectionLoadCts;
    private CancellationTokenSource? _mediaLoadCts;
    private long _inspectionLoadGeneration;
    private long _mediaGeneration;
    private long _activeMediaGeneration;
    private long _videoSurfaceGeneration;
    private string _inspectionLoadStage = "idle";
    private readonly ConcurrentDictionary<string, byte> _reportedLibVlcLogs = new(StringComparer.Ordinal);
    private bool _playerErrorNotified;
    private bool _playerTelemetryErrorNotified;
    private bool _playerDisposed;
    private bool _playerDisposeInProgress;
    private bool _captureInProgress;
    private bool _loadingInspections;
    private bool _sectionReviewInProgress;
    private Point _occurrenceDragStart;
    private Guid? _occurrenceDragId;
    private Guid? _projectDialogProjectId;
    private OccurrenceWindow? _embeddedOccurrenceEditor;
    private bool _inspectionLayoutReady;
    private bool _inspectionSideVisible = true;
    private bool _videoFocusMode;
    private double _inspectionSideWidth = 320;
    private static string InspectionLayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeoAutoDocs", "inspection-layout.json");
    private static string TrayPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeoAutoDocs", "tray-preferences.json");

    public MainWindow(IProjectStore store, WordReportService wordReportService,
        SharedProjectService sharedProjects, LocalLogger logger, FrameCaptureService frameCaptureService,
        OccurrenceService occurrenceService, IAnomalyCatalogService anomalyCatalogService,
        InspectionExchangeService inspectionExchangeService, IInspectionTransport geoAutoEngineTransport,
        BackupStatus backupStatus)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 1480, 900);
        _store = store;
        _wordReportService = wordReportService;
        _sharedProjects = sharedProjects;
        _logger = logger;
        LoadTrayPreferences();
        InitializeTrayIcon();
        _frameCaptureService = frameCaptureService;
        _occurrenceService = occurrenceService;
        _anomalyCatalogService = anomalyCatalogService;
        _inspectionExchangeService = inspectionExchangeService;
        _geoAutoEngineTransport = geoAutoEngineTransport;
        _correctionChannel = geoAutoEngineTransport as IInspectionCorrectionChannel;
        _correctionChannel?.SetCorrectionHandler(ReceiveCorrectionsAsync);
        _backupStatus = backupStatus;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(MediaPlaybackSafety.LibVlcOptions.ToArray());
        _libVlc.Log += OnLibVlcLog;
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        CaptureFrameOverlayButton.Visibility = Visibility.Collapsed;
        CaptureFrameButton.Visibility = Visibility.Visible;
        CaptureFrameButton.Content = "Capturar evidência";
        RefreshBackupStatus();
        _mediaPlayer.EncounteredError += (_, _) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            QueuePlayerUiUpdate(generation, HandlePlayerError);
        };
        _mediaPlayer.Playing += (_, _) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await HandlePlayerPlayingAsync(generation); }
                catch (Exception ex) { LogPlayerControlFailure("callback Playing", ex); }
            }));
        };
        _mediaPlayer.Paused += (_, _) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            QueuePlayerUiUpdate(generation, () => PlayPauseButton.Content = "▶");
        };
        _mediaPlayer.Stopped += (_, _) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            QueuePlayerUiUpdate(generation, () => PlayPauseButton.Content = "▶");
        };
        _mediaPlayer.EndReached += (_, _) =>
        {
            var generation = Volatile.Read(ref _activeMediaGeneration);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await HandlePlayerEndedAsync(generation); }
                catch (Exception ex) { LogPlayerControlFailure("callback EndReached", ex); }
            }));
        };
        ProjectsList.ItemsSource = _projects;
        ReportSectionsList.ItemsSource = _reportSectionItems;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SavePendingAsync(); };
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _syncTimer.Tick += async (_, _) => await CheckpointSharedAsync();
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _heartbeatTimer.Tick += async (_, _) => await HeartbeatAsync();
        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playerTimer.Tick += (_, _) => RefreshPlayerPosition();
        _playerTimer.Start();
        _overviewRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _overviewRefreshTimer.Tick += (_, _) =>
        {
            _overviewRefreshTimer.Stop();
            var watch = Stopwatch.StartNew();
            try
            {
                RefreshOverview();
                _logger.Info($"RefreshOverview adiado concluido. Tempo={watch.ElapsedMilliseconds}ms; " +
                             $"MemoriaGerenciada={GC.GetTotalMemory(false)}; WorkingSet={Environment.WorkingSet}.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Falha no RefreshOverview adiado. Tempo={watch.ElapsedMilliseconds}ms; " +
                              $"MemoriaGerenciada={GC.GetTotalMemory(false)}; WorkingSet={Environment.WorkingSet}.", ex);
            }
        };
        SizeChanged += (_, _) => { ApplyDashboardDensity(); ApplyInspectionLayout(); };
        Loaded += async (_, _) => await ReloadProjectsAsync();
        ContentRendered += (_, _) => _logger.Info("Janela principal exibida.");
        Closing += OnClosing;
        LoadInspectionLayoutPreferences();
        _inspectionLayoutReady = true;
        ApplyInspectionLayout();
    }

    private void QueuePlayerUiUpdate(long generation, Action update)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_playerDisposed || generation <= 0 ||
                generation != Volatile.Read(ref _activeMediaGeneration)) return;
            try { update(); }
            catch (Exception ex) { LogPlayerControlFailure("atualização de estado", ex); }
        }));
    }

    private async Task HandlePlayerPlayingAsync(long generation)
    {
        if (_playerDisposed || generation <= 0) return;
        await _playerLifecycleGate.WaitAsync();
        float? rejectedRate = null;
        try
        {
            if (_playerDisposed || _currentMedia is null ||
                generation != Volatile.Read(ref _activeMediaGeneration)) return;
            VideoEmptyState.Visibility = Visibility.Collapsed;
            VideoLoadedBadge.Visibility = Visibility.Visible;
            UpdatePlayerControlState();
            if (!TryApplyPlaybackRate(_selectedPlaybackRate))
            {
                rejectedRate = _selectedPlaybackRate;
                _selectedPlaybackRate = 1f;
                TryApplyPlaybackRate(1f);
                SelectSpeed(1f);
            }
            PlayPauseButton.Content = "❚❚";
        }
        finally { _playerLifecycleGate.Release(); }

        if (rejectedRate is not null)
            MessageBox.Show($"O formato deste vídeo não aceita {rejectedRate:0.##}x. A velocidade voltou para 1,0x.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task HandlePlayerEndedAsync(long generation)
    {
        if (_playerDisposed || generation <= 0) return;
        await _playerLifecycleGate.WaitAsync();
        try
        {
            if (_playerDisposed || _currentMedia is null ||
                generation != Volatile.Read(ref _activeMediaGeneration)) return;
            _lastKnownPlayerTime = Math.Max(_lastKnownPlayerTime, Math.Max(0, _mediaPlayer.Length));
            PlayPauseButton.Content = "▶";
            RefreshPlayerPositionUnsafe();
        }
        finally { _playerLifecycleGate.Release(); }
    }

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        if (e.Level < LogLevel.Warning) return;
        var message = e.Message.Length <= 800 ? e.Message : e.Message[..800];
        var path = _currentVideo?.FilePath ?? "(nenhum)";
        var key = $"{path}|{e.Level}|{e.Module}|{message}";
        if (!_reportedLibVlcLogs.TryAdd(key, 0)) return;
        _logger.Error(
            $"LibVLC nativo. Nivel={e.Level}; Modulo='{e.Module ?? "desconhecido"}'; Caminho='{path}'.",
            new IOException(message));
    }

    private void LogPlayerControlFailure(string operation, Exception exception)
    {
        if (_playerDisposed) return;
        _logger.Error($"Falha durante {operation} do player. Caminho='{_currentVideo?.FilePath}'.", exception);
    }

    private void LoadTrayPreferences()
    {
        try
        {
            if (!File.Exists(TrayPreferencesPath)) return;
            var preferences = JsonSerializer.Deserialize<TrayPreferences>(
                File.ReadAllText(TrayPreferencesPath));
            if (preferences is null) return;
            _closeToTrayEnabled = preferences.CloseToTray;
            _trayNoticeShown = preferences.NoticeShown;
        }
        catch (Exception ex)
        {
            _logger.Info($"Preferências da bandeja ignoradas: {ex.Message}");
        }
    }

    private void SaveTrayPreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TrayPreferencesPath)!);
            File.WriteAllText(TrayPreferencesPath, JsonSerializer.Serialize(
                new TrayPreferences(_closeToTrayEnabled, _trayNoticeShown)));
        }
        catch (Exception ex)
        {
            _logger.Info($"Não foi possível salvar as preferências da bandeja: {ex.Message}");
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/GeoAutoDocs.ico"));
            if (resource?.Stream is not null)
            {
                using (resource.Stream)
                using (var sourceIcon = new Drawing.Icon(resource.Stream))
                    _trayIconImage = (Drawing.Icon)sourceIcon.Clone();
            }
            _trayIconImage ??= new Drawing.Icon(Drawing.SystemIcons.Application, 32, 32);

            var openItem = new WinForms.ToolStripMenuItem("Abrir GeoAutoDocs");
            openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));

            _trayPendingCapturesItem = new WinForms.ToolStripMenuItem("Capturas pendentes")
            {
                Enabled = false
            };
            _trayPendingCapturesItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                RestoreFromTray();
                OpenPendingCaptures_Click(this, new RoutedEventArgs());
            }));

            _closeToTrayMenuItem = new WinForms.ToolStripMenuItem("Fechar para a bandeja")
            {
                CheckOnClick = true,
                Checked = _closeToTrayEnabled
            };
            _closeToTrayMenuItem.CheckedChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _closeToTrayEnabled = _closeToTrayMenuItem.Checked;
                SaveTrayPreferences();
                _logger.Info($"Fechar para a bandeja alterado. Ativo={_closeToTrayEnabled}.");
            }));

            var exitItem = new WinForms.ToolStripMenuItem("Sair");
            exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(RequestApplicationExit));

            _trayMenu = new WinForms.ContextMenuStrip();
            _trayMenu.Items.Add(openItem);
            _trayMenu.Items.Add(_trayPendingCapturesItem);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            _trayMenu.Items.Add(_closeToTrayMenuItem);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            _trayMenu.Items.Add(exitItem);

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "GeoAutoDocs — clique duas vezes para abrir",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
        }
        catch (Exception ex)
        {
            _logger.Error("Não foi possível criar o ícone da bandeja.", ex);
            DisposeTrayIcon();
        }
    }

    private async Task<bool> HideToTrayAsync()
    {
        _saveTimer.Stop();
        if (_hasPendingChanges && !await SavePendingAsync())
        {
            SaveStatus.Text = "Não foi possível ocultar — alterações ainda não foram salvas";
            return false;
        }

        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        ShowInTaskbar = false;
        Hide();
        _logger.Info("Janela principal ocultada na bandeja do Windows.");

        if (!_trayNoticeShown && _trayIcon is not null)
        {
            _trayNoticeShown = true;
            SaveTrayPreferences();
            _trayIcon.ShowBalloonTip(4000, "GeoAutoDocs continua aberto",
                "Clique duas vezes no ícone para reabrir ou use “Sair” para encerrar.",
                WinForms.ToolTipIcon.Info);
        }

        return true;
    }

    private void RestoreFromTray()
    {
        if (_closingAfterSave || _playerDisposed) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
        _logger.Info("Janela principal restaurada a partir da bandeja do Windows.");
    }

    public void RequestApplicationExit()
    {
        _exitRequested = true;
        RestoreFromTray();
        Close();
    }

    public void PrepareForSystemShutdown() => _exitRequested = true;

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayPendingCapturesItem = null;
        _closeToTrayMenuItem = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
    }

    private void LoadInspectionLayoutPreferences()
    {
        try
        {
            if (!File.Exists(InspectionLayoutPath)) return;
            var value = JsonSerializer.Deserialize<InspectionLayoutPreferences>(File.ReadAllText(InspectionLayoutPath));
            if (value is null) return;
            _inspectionSideWidth = Math.Clamp(value.SideWidth, 290, 400);
            _videoFocusMode = value.FocusMode;
            InspectionContextExpander.IsExpanded = value.ContextExpanded;
        }
        catch (Exception ex) { _logger.Info($"Preferências do player ignoradas: {ex.Message}"); }
    }

    private void SaveInspectionLayoutPreferences()
    {
        if (!_inspectionLayoutReady) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InspectionLayoutPath)!);
            File.WriteAllText(InspectionLayoutPath, JsonSerializer.Serialize(new InspectionLayoutPreferences(
                _inspectionSideWidth, _inspectionSideVisible, InspectionContextExpander.IsExpanded, _videoFocusMode)));
        }
        catch (Exception ex) { _logger.Info($"Não foi possível salvar o layout do player: {ex.Message}"); }
    }

    private void ApplyInspectionLayout()
    {
        var showSide = !_videoFocusMode;
        EditorHeaderGrid.Visibility = _videoFocusMode ? Visibility.Collapsed : Visibility.Visible;
        InspectionContextExpander.Visibility = _videoFocusMode ? Visibility.Collapsed : Visibility.Visible;
        InspectionSidePanel.Visibility = showSide ? Visibility.Visible : Visibility.Collapsed;
        InspectionSideSplitter.Visibility = showSide ? Visibility.Visible : Visibility.Collapsed;
        InspectionSplitterColumn.Width = showSide ? new GridLength(6) : new GridLength(0);
        var responsiveWidth = ActualWidth > 0 && ActualWidth < 1100
            ? Math.Min(_inspectionSideWidth, 300) : _inspectionSideWidth;
        InspectionSideColumn.Width = showSide ? new GridLength(responsiveWidth) : new GridLength(0);
        FocusVideoButton.Content = _videoFocusMode ? "Sair do modo foco" : "Modo foco";
    }

    private void FocusVideo_Click(object sender, RoutedEventArgs e) => ToggleVideoFocus();

    private void VideoView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ToggleVideoFocus();
        e.Handled = true;
    }

    private void ToggleVideoFocus()
    {
        _videoFocusMode = !_videoFocusMode;
        ApplyInspectionLayout();
        SaveInspectionLayoutPreferences();
    }

    private void InspectionSideSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (InspectionSideColumn.ActualWidth >= 290)
            _inspectionSideWidth = Math.Clamp(InspectionSideColumn.ActualWidth, 290, 400);
        SaveInspectionLayoutPreferences();
    }

    private void InspectionLayoutPreferenceChanged(object sender, RoutedEventArgs e) =>
        SaveInspectionLayoutPreferences();

    private async Task ReloadProjectsAsync()
    {
        try
        {
            _projects.Clear();
            foreach (var project in await _store.ListAsync()) _projects.Add(project);
            CollectionViewSource.GetDefaultView(_projects).Refresh();
            RefreshDashboardMetrics();
            RefreshHomeStatus();
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
        view.Filter = item => item is Project project && ProjectDashboard.Matches(project, query);
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
        NewProjectTopButton.Visibility = Visibility.Visible;
    }

    private void RefreshDashboardMetrics()
    {
        var summary = ProjectDashboard.Summarize(_projects);
        var projectCount = summary.Projects;
        var reportCount = summary.Reports;
        var inProgressCount = summary.InProgressReports;
        var sharedCount = summary.SharedProjects;

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

    private void RefreshHomeStatus()
    {
        var sharedCount = _projects.Count(x => x.StorageKind == ProjectStorageKind.Shared);
        StorageValueText.Text = sharedCount == 0 ? "Local" : sharedCount == _projects.Count ? "Compartilhado" : "Misto";
        StorageDetailText.Text = sharedCount == 0
            ? "Projetos salvos neste computador"
            : $"{sharedCount} projeto(s) com dados compartilhados";
        SecurityValueText.Text = _saveErrorNotified ? "Falha no último salvamento" : "Salvamento automático ativo";
        SecurityValueText.Foreground = (System.Windows.Media.Brush)FindResource(
            _saveErrorNotified ? "DangerBrush" : "TextPrimaryBrush");
        SecurityDetailText.Text = _saveErrorNotified
            ? "Consulte o log antes de continuar"
            : "Alterações são persistidas automaticamente";
    }

    private void RefreshBackupStatus()
    {
        if (_backupStatus.HasSuccessfulBackup)
        {
            BackupValueText.Text = _backupStatus.LastSuccessfulAt!.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
            BackupDetailText.Text = _backupStatus.LastError is null
                ? "Backup automático concluído"
                : "Última tentativa falhou; backup anterior preservado";
            BackupDetailText.Foreground = (System.Windows.Media.Brush)FindResource(
                _backupStatus.LastError is null ? "TextMutedBrush" : "WarningBrush");
            return;
        }
        BackupValueText.Text = "Nunca realizado";
        BackupDetailText.Text = _backupStatus.LastError is null ? "Nenhum backup disponível" : "Falha ao criar backup";
        BackupDetailText.Foreground = (System.Windows.Media.Brush)FindResource(
            _backupStatus.LastError is null ? "TextMutedBrush" : "DangerBrush");
    }

    public void UpdateBackupStatus(BackupStatus status)
    {
        _backupStatus = status;
        RefreshBackupStatus();
    }

    private void ApplyDashboardDensity()
    {
        var compact = ActualWidth > 0 && ActualWidth < 1100;
        var compactEditor = ActualWidth > 0 && ActualWidth < 1050;
        EditorView.Margin = compactEditor ? new Thickness(8, 8, 8, 6) : new Thickness(22, 12, 20, 10);
        BackToProjectsText.Visibility = compactEditor ? Visibility.Collapsed : Visibility.Visible;
        ShareProjectText.Visibility = compactEditor ? Visibility.Collapsed : Visibility.Visible;
        StorageStatus.Visibility = compactEditor ? Visibility.Collapsed : Visibility.Visible;
        var padding = compact ? new Thickness(8, 14, 8, 14) : new Thickness(16, 14, 16, 14);
        foreach (var card in new[] { TotalProjectsCard, TotalReportsCard, InProgressReportsCard, SharedProjectsCard })
            card.Padding = padding;
        foreach (var column in new[] { TotalProjectsIconColumn, TotalReportsIconColumn, InProgressReportsIconColumn, SharedProjectsIconColumn })
            column.Width = new GridLength(compact ? 38 : 42);
        foreach (var label in new[] { TotalProjectsLabel, TotalReportsLabel, InProgressReportsLabel, SharedProjectsLabel })
            label.FontSize = compact ? 9.5 : 11;
        foreach (var detail in new[] { TotalProjectsDetail, TotalReportsDetail, InProgressReportsDetail, SharedProjectsDetail })
            detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ProjectsHeaderGrid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(ProjectsHeaderTitlePanel, compact ? 3 : 1);
        Grid.SetRow(ProjectsHeaderSearchPanel, compact ? 1 : 0);
        Grid.SetColumn(ProjectsHeaderSearchPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(ProjectsHeaderSearchPanel, compact ? 2 : 1);
        ProjectsHeaderSearchPanel.Margin = compact ? new Thickness(0, 18, 12, 0) : new Thickness(18, 0, 18, 0);
        ProjectsSearchColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(390);
        Grid.SetRow(NewProjectTopButton, compact ? 1 : 0);
        NewProjectTopButton.Margin = compact ? new Thickness(0, 18, 0, 0) : new Thickness(0);
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
            UpdateSidebarNavigation();
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
        await StopAndReleaseCurrentMediaAsync();
        _currentProject = null; _currentReport = null; _sharedSession = null;
        EditorView.Visibility = Visibility.Collapsed;
        HomeShell.Visibility = Visibility.Visible;
        NewProjectView.Visibility = Visibility.Collapsed;
        ProjectsView.Visibility = Visibility.Visible;
        UpdateSidebarNavigation();
        await ReloadProjectsAsync();
    }

    private void SidebarProjects_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is not null && EditorView.Visibility == Visibility.Visible)
            OverviewTab.IsSelected = true;
        UpdateSidebarNavigation();
    }

    private void SidebarInspection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        InspectionsTab.IsSelected = true;
        UpdateSidebarNavigation();
    }

    private void SidebarReports_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        WordConfigTab.IsSelected = true;
        UpdateSidebarNavigation();
    }

    private void UpdateSidebarNavigation()
    {
        if (SidebarProjectsButton is null || SidebarInspectionButton is null || SidebarReportsButton is null ||
            OverviewTab is null || InspectionsTab is null || WordConfigTab is null)
            return;
        var projectOpen = _currentProject is not null && EditorView.Visibility == Visibility.Visible;
        SidebarInspectionButton.IsEnabled = projectOpen;
        SidebarReportsButton.IsEnabled = projectOpen;

        var projectsActive = !projectOpen || OverviewTab.IsSelected;
        SidebarProjectsButton.Style = (Style)FindResource(
            projectsActive ? "SidebarItemActiveStyle" : "SidebarItemStyle");
        SidebarInspectionButton.Style = (Style)FindResource(
            projectOpen && InspectionsTab.IsSelected ? "SidebarItemActiveStyle" : "SidebarItemStyle");
        SidebarReportsButton.Style = (Style)FindResource(
            projectOpen && WordConfigTab.IsSelected ? "SidebarItemActiveStyle" : "SidebarItemStyle");
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
        ContinueReportButton.IsEnabled = !IsReadOnly && _currentProject is not null;
        HeaderNewReportButton.IsEnabled = _sharedSession?.IsReadOnly != true;
        GenerateReportButton.IsEnabled = !IsReadOnly && _currentReport is not null;
        ReportsEmptyState.Visibility = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReportHeaderTab.IsEnabled = !IsReadOnly && _currentReport is not null;
        WordConfigTab.IsEnabled = true;
        ReadOnlyInspectionText.Visibility = IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyInspectionBanner.Visibility = IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        RefreshInspectionTree();
        UpdateInspectionWriteState();
        LoadReport(_currentReport);
        RefreshFilePreviews();
        RefreshOverview();
        OverviewTab.IsSelected = true;
        UpdateSidebarNavigation();
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
        DeleteReportButton.IsEnabled = report is not null && !IsReadOnly;
        GenerateReportButton.IsEnabled = report is not null && !IsReadOnly;
        ContinueReportButton.IsEnabled = _currentProject is not null && !IsReadOnly;
        SendToGeoAutoEngineButton.IsEnabled = report is not null;
        ExportInspectionButton.IsEnabled = report is not null;
        GeoAutoEngineStatusText.Text = "";
        ContinueReportButton.ToolTip = IsReadOnly
            ? "Projeto aberto somente para leitura."
            : "Selecione um DOCX existente para analisar e continuar.";
        ReadOnlyReportBanner.Visibility = IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
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
        MarkPendingChanges();
        await SavePendingAsync();
        ReportCodeBox.Focus();
        ReportCodeBox.SelectAll();
    }

    private async void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentReport is not null) await DeleteReportAsync(_currentReport);
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível excluir o relatório.", ex);
        }
    }

    private async void DeleteReportCard_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (_currentProject is null || sender is not Button { Tag: Guid reportId }) return;
            var report = _currentProject.Reports.SingleOrDefault(x => x.Id == reportId);
            if (report is not null) await DeleteReportAsync(report);
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível excluir o relatório selecionado.", ex);
        }
    }

    private async Task DeleteReportAsync(Report report)
    {
        var project = _currentProject;
        if (project is null || _reportDeletionInProgress || !project.Reports.Contains(report)) return;
        if (IsReadOnly)
        {
            MessageBox.Show("Este projeto está aberto somente para leitura. Não é possível excluir relatórios.",
                "Excluir relatório", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Excluir o relatório '{report.DisplayCode}'?\n\n" +
                            "Os dados de preparação serão removidos do projeto. O arquivo DOCX já gerado será preservado.",
                "Excluir relatório", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        ApplyFieldsToModel();
        var originalIndex = project.Reports.IndexOf(report);
        var originalSelection = _currentReport;
        _reportDeletionInProgress = true;
        ReportsList.IsEnabled = false;
        NewReportButton.IsEnabled = false;
        HeaderNewReportButton.IsEnabled = false;
        DeleteReportButton.IsEnabled = false;

        try
        {
            project.Reports.RemoveAt(originalIndex);
            var remaining = project.Reports.OrderBy(x => x.Order).ToList();
            var nextSelection = originalSelection is not null && originalSelection.Id != report.Id &&
                                remaining.Contains(originalSelection)
                ? originalSelection
                : remaining.ElementAtOrDefault(Math.Min(originalIndex, Math.Max(0, remaining.Count - 1)));
            RefreshReportListSelection(nextSelection);
            DeleteReportButton.IsEnabled = false;

            MarkPendingChanges();
            if (await SavePendingAsync())
            {
                _logger.Info($"Relatório excluído. ProjetoId='{project.Id}'; RelatórioId='{report.Id}'; " +
                             $"Código='{report.DisplayCode}'; ArquivoGeradoPreservado='{report.GeneratedReportPath}'.");
                return;
            }

            project.Reports.Insert(Math.Min(originalIndex, project.Reports.Count), report);
            RefreshReportListSelection(originalSelection ?? report);
            MarkPendingChanges();
            _logger.Info($"Exclusão do relatório revertida após falha de salvamento. " +
                         $"ProjetoId='{project.Id}'; RelatórioId='{report.Id}'.");
            MessageBox.Show("A exclusão não foi concluída porque o projeto não pôde ser salvo. " +
                            "O relatório foi restaurado na lista.",
                "Excluir relatório", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            if (!project.Reports.Contains(report))
                project.Reports.Insert(Math.Min(originalIndex, project.Reports.Count), report);
            RefreshReportListSelection(originalSelection ?? report);
            MarkPendingChanges();
            throw;
        }
        finally
        {
            _reportDeletionInProgress = false;
            ReportsList.IsEnabled = true;
            var canEdit = _currentProject is not null && !IsReadOnly;
            NewReportButton.IsEnabled = canEdit;
            HeaderNewReportButton.IsEnabled = canEdit;
            DeleteReportButton.IsEnabled = canEdit && _currentReport is not null;
        }
    }

    private void RefreshReportListSelection(Report? selectedReport)
    {
        if (_currentProject is null) return;
        var reports = _currentProject.Reports.OrderBy(x => x.Order).ToList();
        _currentReport = selectedReport is not null && reports.Contains(selectedReport)
            ? selectedReport
            : reports.FirstOrDefault();

        _loadingFields = true;
        try
        {
            ReportsCombo.ItemsSource = reports;
            ReportsList.ItemsSource = reports;
            ReportsCombo.SelectedItem = _currentReport;
            ReportsList.SelectedItem = _currentReport;
            ReportsEmptyState.Visibility = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loadingFields = false;
        }

        LoadReport(_currentReport);
        RefreshOverview();
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
        MarkPendingChanges();
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

    private async Task<bool> SavePendingAsync()
    {
        _saveTimer.Stop();
        await _persistenceGate.WaitAsync();
        try
        {
            if (!_hasPendingChanges || _currentProject is null || _sharedSession?.IsReadOnly == true)
                return !_saveErrorNotified;
            try
            {
                do
                {
                    var saveRevision = _pendingSaveRevision;
                    ApplyFieldsToModel();
                    await _store.SaveAsync(_currentProject);
                    if (_sharedSession is not null)
                    {
                        await _sharedSession.SaveLocalAsync();
                        _sharedDirty = true;
                    }
                    _hasPendingChanges = _pendingSaveRevision != saveRevision;
                }
                while (_hasPendingChanges);

                _saveErrorNotified = false;
                RefreshHomeStatus();
                ProjectClientSummary.Text = string.IsNullOrWhiteSpace(_currentProject.DefaultClient)
                    ? "Dados do cliente ainda não informados" : _currentProject.DefaultClient;
                ActiveReportTitle.Text = _currentReport?.DisplayCode ?? "Selecione um relatório";
                ReportsList.Items.Refresh();
                RefreshOverview();
                RefreshReportReview();
                SaveStatus.Text = "✓ Salvo";
                UpdateSectionReviewState();
                return true;
            }
            catch (Exception ex)
            {
                SaveStatus.Text = "Falha ao salvar — consulte o log";
                UpdateSectionReviewState();
                _logger.Error("Não foi possível salvar as alterações.", ex);
                if (_saveErrorNotified) return false;
                _saveErrorNotified = true;
                RefreshHomeStatus();
                MessageBox.Show("Não foi possível salvar as alterações. Consulte o log.",
                    "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task CheckpointSharedAsync()
    {
        if (_checkpointInProgress || _finalizationInProgress ||
            _sharedSession is null || _sharedSession.IsReadOnly || !_sharedDirty) return;

        _checkpointInProgress = true;
        try
        {
            if (!await SavePendingAsync())
            {
                _sharedDirty = true;
                SaveStatus.Text = "Sincronização adiada — alterações locais não foram salvas";
                return;
            }

            await _persistenceGate.WaitAsync();
            try
            {
                if (_sharedSession is null || _sharedSession.IsReadOnly || !_sharedDirty) return;
                await _sharedSession.CheckpointAsync();
                _sharedDirty = _hasPendingChanges;
                SaveStatus.Text = _sharedDirty
                    ? "Alterações pendentes de sincronização"
                    : "Sincronizado com o servidor";
            }
            finally
            {
                _persistenceGate.Release();
            }
        }
        catch (Exception ex)
        {
            _sharedDirty = true;
            SaveStatus.Text = "Servidor indisponível — alterações protegidas localmente";
            _logger.Error("Falha no checkpoint compartilhado.", ex);
        }
        finally
        {
            _checkpointInProgress = false;
        }
    }

    private async Task HeartbeatAsync()
    {
        try { if (_sharedSession is { IsReadOnly: false }) await _sharedSession.HeartbeatAsync(); }
        catch (Exception ex) { SaveStatus.Text = "Servidor indisponível — alterações protegidas localmente"; _logger.Error("Falha no heartbeat.", ex); }
    }

    private async Task<bool> FinalizeCurrentProjectAsync()
    {
        if (_finalizationInProgress) return false;
        _finalizationInProgress = true;
        var session = _sharedSession;
        var restoreSharedTimers = session is { IsReadOnly: false };
        var completed = false;
        if (restoreSharedTimers) SetSharedTimers(false);

        try
        {
            if (!await SavePendingAsync())
            {
                SaveStatus.Text = "Fechamento cancelado — alterações ainda não foram salvas";
                return false;
            }

            if (session is not null)
            {
                await session.CloseAsync();
                _sharedDirty = false;
            }

            _frameCaptureService.Discard(_captureDraft);
            _captureDraft = null;
            await StopAndReleaseCurrentMediaAsync();
            SetSharedTimers(false);
            completed = true;
            return true;
        }
        catch (Exception ex)
        {
            _sharedDirty = _hasPendingChanges || session is { IsReadOnly: false, IsClosed: false };
            ShowError("A sincronização final falhou. O projeto permanece aberto.", ex);
            return false;
        }
        finally
        {
            if (!completed && restoreSharedTimers && session is { IsClosed: false })
                SetSharedTimers(true);
            _finalizationInProgress = false;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveInspectionLayoutPreferences();
        if (_closingAfterSave && _playerDisposed)
        {
            DisposeTrayIcon();
            return;
        }

        e.Cancel = true;
        if (_playerDisposeInProgress || _finalizationInProgress || _hideToTrayInProgress) return;

        if (!_exitRequested && _closeToTrayEnabled && _trayIcon is not null)
        {
            _hideToTrayInProgress = true;
            try
            {
                if (!await HideToTrayAsync())
                {
                    ShowInTaskbar = true;
                    Show();
                    Activate();
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Falha ao ocultar o GeoAutoDocs na bandeja.", ex);
                ShowInTaskbar = true;
                Show();
                Activate();
                MessageBox.Show("Não foi possível enviar o GeoAutoDocs para a bandeja. Consulte o log.",
                    "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _hideToTrayInProgress = false;
            }
            return;
        }

        try
        {
            if ((_currentProject is not null || _hasPendingChanges) &&
                !await FinalizeCurrentProjectAsync())
            {
                _exitRequested = false;
                return;
            }
            if (_correctionChannel is not null)
                await _correctionChannel.StopCorrectionSessionAsync();
            await DisposePlayerAsync();
            _closingAfterSave = true;
            DisposeTrayIcon();
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            _exitRequested = false;
            _logger.Error("Falha ao finalizar o player durante o fechamento.", ex);
            MessageBox.Show("Não foi possível concluir o fechamento com segurança. Consulte o log.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ShareProject_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do projeto compartilhado", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;

        var project = _currentProject;
        var previousStorageKind = project.StorageKind;
        var previousSharedPath = project.SharedProjectPath;
        var previousVersion = project.ProjectDataVersion;
        SharedProjectSession? openedSession = null;
        try
        {
            ApplyFieldsToModel();
            if (!await SavePendingAsync()) return;
            openedSession = await _sharedProjects.OpenAsync(project, dialog.FolderName);
            await _store.SaveAsync(openedSession.Project);
            _sharedSession = openedSession;
            _currentProject = openedSession.Project;
            LoadProject(_currentProject);
            SetSharedTimers(true);
        }
        catch (Exception ex)
        {
            SetSharedTimers(false);
            var sessionReleased = openedSession is null;
            if (openedSession is { IsClosed: false })
            {
                try
                {
                    await openedSession.AbandonAsync();
                    sessionReleased = true;
                }
                catch (Exception releaseError)
                {
                    _logger.Error("Falha ao liberar lock após compartilhamento incompleto.", releaseError);
                }
            }

            if (sessionReleased)
            {
                project.StorageKind = previousStorageKind;
                project.SharedProjectPath = previousSharedPath;
                project.ProjectDataVersion = previousVersion;
                _sharedSession = null;
                _currentProject = project;
                _sharedDirty = false;
            }
            else
            {
                _sharedSession = openedSession;
                _currentProject = openedSession!.Project;
                _sharedDirty = true;
                LoadProject(_currentProject);
                SetSharedTimers(true);
            }
            ShowError("Não foi possível compartilhar o projeto.", ex);
        }
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
        if (!await SavePendingAsync()) return;
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
            var persisted = true;
            if (_sharedSession?.IsReadOnly != true)
            {
                MarkPendingChanges();
                persisted = await SavePendingAsync();
            }
            GeneratedReportText.Text = persisted
                ? $"✓ Relatório gerado\n{Path.GetFileName(path)}"
                : $"⚠ Relatório gerado, mas o estado permanece pendente de salvamento\n{Path.GetFileName(path)}";
            OpenReportButton.IsEnabled = true; OpenReportFolderButton.IsEnabled = true;
            ReportsList.Items.Refresh();
            RefreshOverview();
            RefreshReportReview();
        }
        catch (InspectionExchangeExportException ex)
        {
            var persisted = true;
            if (_sharedSession?.IsReadOnly != true)
            {
                MarkPendingChanges();
                persisted = await SavePendingAsync();
            }
            GeneratedReportText.Text = persisted
                ? $"⚠ DOCX gerado; falha no arquivo de intercâmbio\n{Path.GetFileName(ex.DocxPath)}"
                : $"⚠ DOCX gerado; intercâmbio falhou e o estado permanece pendente de salvamento\n{Path.GetFileName(ex.DocxPath)}";
            OpenReportButton.IsEnabled = true;
            OpenReportFolderButton.IsEnabled = true;
            ReportsList.Items.Refresh();
            RefreshOverview();
            ShowError(ex.Message, ex);
        }
        catch (Exception ex) { GeneratedReportText.Text = "Falha ao gerar relatório."; ShowError(ex.Message, ex); }
    }

    private async void ExportInspection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentReport is null) return;
        try
        {
            if (!IsReadOnly)
            {
                ApplyFieldsToModel();
                if (!await SavePendingAsync()) return;
            }
            var document = _inspectionExchangeService.Build(_currentProject, _currentReport);
            var reportsDirectory = Path.Combine(_currentProject.ProjectPath ?? "", "reports");
            var dialog = new SaveFileDialog
            {
                Title = "Exportar inspeção estruturada",
                Filter = "Intercâmbio GeoAutoDocs (*.geoadocs.json)|*.geoadocs.json",
                AddExtension = true,
                DefaultExt = ".geoadocs.json",
                FileName = SanitizeSuggestedFileName($"{_currentReport.DisplayCode}.geoadocs.json"),
                InitialDirectory = Directory.Exists(reportsDirectory) ? reportsDirectory : null,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            var transport = new FileInspectionTransport(dialog.FileName, _inspectionExchangeService.Serializer);
            var result = await transport.SendAsync(document);
            GeoAutoEngineStatusText.Text = result.Success
                ? $"✓ Arquivo exportado: {Path.GetFileName(result.Destination)}"
                : result.Message;
            GeoAutoEngineStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                result.Success ? "SuccessBrush" : "WarningBrush");
            if (!result.Success) ShowError(result.Message, new IOException(result.Message));
        }
        catch (Exception ex)
        {
            GeoAutoEngineStatusText.Text = "Não foi possível exportar a inspeção.";
            ShowError(ex.Message, ex);
        }
    }

    private async void SendToGeoAutoEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentReport is null) return;
        SendToGeoAutoEngineButton.IsEnabled = false;
        GeoAutoEngineStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        GeoAutoEngineStatusText.Text = "Procurando GeoAutoEngine e verificando compatibilidade...";
        try
        {
            if (!IsReadOnly)
            {
                ApplyFieldsToModel();
                if (!await SavePendingAsync())
                {
                    GeoAutoEngineStatusText.Text = "Envio cancelado — alterações locais não foram salvas.";
                    GeoAutoEngineStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                    return;
                }
            }
            var document = _inspectionExchangeService.Build(_currentProject, _currentReport);
            var result = await _geoAutoEngineTransport.SendAsync(document);
            GeoAutoEngineStatusText.Text = result.Message;
            GeoAutoEngineStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                result.Success ? "SuccessBrush" : "WarningBrush");
            if (result.Success)
            {
                var occurrences = document.Sections.Sum(x => x.Occurrences.Count);
                MessageBox.Show($"GeoAutoEngine encontrado\n\n{document.Report.ReportCode}\nenviado com sucesso.\n\n" +
                                $"{document.Sections.Count} trecho(s)\n{occurrences} ocorrência(s)",
                    "Enviar ao GeoAutoEngine", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var fallback = result.Status == InspectionTransportStatus.DestinationUnavailable
                ? "\n\nAbra o Civil 3D com o GeoAutoEngine carregado ou use “Exportar .geoadocs.json”."
                : string.Empty;
            MessageBox.Show(result.Message + fallback, "Enviar ao GeoAutoEngine",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            GeoAutoEngineStatusText.Text = "Não foi possível preparar o envio ao GeoAutoEngine.";
            ShowError(ex.Message, ex);
        }
        finally
        {
            SendToGeoAutoEngineButton.IsEnabled = _currentReport is not null;
        }
    }

    private async void ContinueReport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || IsReadOnly) return;

        var report = _currentReport;
        var createdForContinuation = false;
        if (report is null)
        {
            var sections = _currentProject.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name)
                .SelectMany(x => x.Sections.OrderBy(section => section.Order).ThenBy(section => section.Name))
                .ToList();
            if (sections.Count == 0)
            {
                MessageBox.Show("Cadastre ao menos um trecho antes de continuar um relatório.", "GeoAutoDocs",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            report = _currentProject.CreateReport("Continuação de relatório");
            report.TemplatePath = _wordReportService.DefaultTemplatePath;
            foreach (var section in sections) report.SelectSection(section);
            createdForContinuation = true;
        }
        else
        {
            ApplyFieldsToModel();
            if (!await SavePendingAsync()) return;
        }

        var window = new ContinueReportWindow(_currentProject, report, _wordReportService) { Owner = this };
        if (window.ShowDialog() != true || window.Result is null)
        {
            if (createdForContinuation) _currentProject.Reports.Remove(report);
            return;
        }

        var result = window.Result;
        if (createdForContinuation)
        {
            report.Name = Path.GetFileNameWithoutExtension(result.BaseFilePath);
            _currentReport = report;
            _loadingFields = true;
            var reports = _currentProject.Reports.OrderBy(x => x.Order).ToList();
            ReportsCombo.ItemsSource = reports;
            ReportsList.ItemsSource = reports;
            ReportsCombo.SelectedItem = report;
            ReportsList.SelectedItem = report;
            ReportsEmptyState.Visibility = Visibility.Collapsed;
            _loadingFields = false;
        }

        report.GeneratedReportPath = result.OutputPath;
        report.ContinuationExports.Add(new ReportContinuationExport
        {
            ReportId = report.Id,
            BaseFilePath = result.BaseFilePath,
            BaseFileHash = result.BaseFileHash,
            GeneratedFilePath = result.OutputPath,
            Version = result.Version,
            GeneratedAt = DateTime.UtcNow,
            ManifestJson = result.ManifestJson,
            Status = "Concluído",
            Message = $"{result.AddedOccurrences} anomalia(s), {result.AddedEvidences} evidência(s)"
        });
        MarkPendingChanges();
        if (!await SavePendingAsync()) return;
        LoadReport(report);
        ReportsList.Items.Refresh();
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
        var wasRefreshing = _refreshingReportSectionSelection;
        _refreshingReportSectionSelection = true;
        try
        {
            _reportSectionItems.Clear();
            if (_currentProject is null)
            {
                UpdateReportSectionBulkButtons();
                return;
            }

            var selectedIds = _currentReport?.SectionLinks.Select(x => x.SectionId).ToHashSet() ?? [];
            foreach (var location in _currentProject.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name))
            foreach (var section in location.Sections.OrderBy(x => x.Order).ThenBy(x => x.Name))
                _reportSectionItems.Add(new ReportSectionSelectionItem(
                    section.Id, $"{location.Name} — {section.Name}", selectedIds.Contains(section.Id)));
            UpdateReportSectionBulkButtons();
        }
        finally
        {
            _refreshingReportSectionSelection = wasRefreshing;
        }
    }

    private void UpdateReportSectionBulkButtons()
    {
        var canChange = !_reportSectionBulkUpdateInProgress && _currentReport is not null &&
                        _currentProject is not null && !IsReadOnly && _reportSectionItems.Count > 0;
        SelectAllReportSectionsButton.IsEnabled = canChange && _reportSectionItems.Any(x => !x.IsSelected);
        ClearReportSectionsButton.IsEnabled = canChange && _reportSectionItems.Any(x => x.IsSelected);
    }

    private void ReportSectionSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingFields || _loadingInspections || _refreshingReportSectionSelection ||
            _currentProject is null || _currentReport is null || IsReadOnly ||
            sender is not CheckBox { Tag: Guid sectionId } checkBox) return;
        var section = _currentProject.Locations.SelectMany(x => x.Sections).SingleOrDefault(x => x.Id == sectionId);
        if (section is null) return;
        if (checkBox.IsChecked == true) _currentReport.SelectSection(section);
        else _currentReport.SectionLinks.RemoveAll(x => x.SectionId == sectionId);
        NormalizeReportSectionOrder();
        RefreshReportReview();
        UpdateReportSectionBulkButtons();
        ScheduleSave();
    }

    private async void SelectAllReportSections_Click(object sender, RoutedEventArgs e) =>
        await SetAllReportSectionsAsync(true);

    private async void ClearReportSections_Click(object sender, RoutedEventArgs e) =>
        await SetAllReportSectionsAsync(false);

    private async Task SetAllReportSectionsAsync(bool selected)
    {
        if (_reportSectionBulkUpdateInProgress) return;
        if (_currentProject is null || _currentReport is null)
        {
            MessageBox.Show("Selecione um relatório antes de alterar seus trechos.",
                "Trechos do relatório", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (IsReadOnly)
        {
            MessageBox.Show("Este projeto está aberto somente para leitura.",
                "Trechos do relatório", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var project = _currentProject;
        var report = _currentReport;
        var previousLinks = report.SectionLinks.ToList();
        var previousSelection = previousLinks.Select(x => x.SectionId).ToHashSet();
        var orderedSections = project.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name)
            .SelectMany(x => x.Sections.OrderBy(y => y.Order).ThenBy(y => y.Name))
            .ToList();
        var expectedCount = selected ? orderedSections.Count : 0;
        if (report.SectionLinks.Count == expectedCount &&
            (!selected || orderedSections.All(x => previousSelection.Contains(x.Id))))
        {
            RefreshReportSectionSelection();
            return;
        }

        _reportSectionBulkUpdateInProgress = true;
        _refreshingReportSectionSelection = true;
        UpdateReportSectionBulkButtons();
        try
        {
            ReportSectionSelection.SetAll(report, orderedSections, selected);
            foreach (var item in _reportSectionItems)
                item.IsSelected = selected;
            RefreshReportReview();

            MarkPendingChanges();
            SaveStatus.Text = selected ? "Selecionando todos os trechos..." : "Limpando trechos do relatório...";
            if (!await SavePendingAsync())
            {
                report.SectionLinks.Clear();
                report.SectionLinks.AddRange(previousLinks);
                foreach (var item in _reportSectionItems)
                    item.IsSelected = previousSelection.Contains(item.SectionId);
                MarkPendingChanges();
                RefreshReportReview();
                _logger.Info($"Seleção em massa revertida após falha de salvamento. " +
                             $"RelatórioId='{report.Id}'; Selecionar={selected}.");
                return;
            }

            _logger.Info(selected
                ? $"Todos os trechos do relatório foram selecionados e persistidos. RelatórioId='{report.Id}'; Trechos={expectedCount}."
                : $"Todos os trechos do relatório foram removidos e a alteração foi persistida. RelatórioId='{report.Id}'.");
        }
        catch (Exception ex)
        {
            report.SectionLinks.Clear();
            report.SectionLinks.AddRange(previousLinks);
            foreach (var item in _reportSectionItems)
                item.IsSelected = previousSelection.Contains(item.SectionId);
            RefreshReportReview();
            _logger.Error("Falha ao alterar todos os trechos do relatório.", ex);
            MessageBox.Show("Não foi possível alterar os trechos do relatório. A seleção anterior foi restaurada.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refreshingReportSectionSelection = false;
            _reportSectionBulkUpdateInProgress = false;
            UpdateReportSectionBulkButtons();
        }
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

    private Task<InspectionCorrectionAcknowledgement> ReceiveCorrectionsAsync(
        InspectionCorrectionPackage package, CancellationToken cancellationToken)
    {
        if (Dispatcher.CheckAccess())
            return ReviewAndApplyCorrectionsAsync(package, cancellationToken);

        return Dispatcher.InvokeAsync(
            () => ReviewAndApplyCorrectionsAsync(package, cancellationToken),
            DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    private async Task<InspectionCorrectionAcknowledgement> ReviewAndApplyCorrectionsAsync(
        InspectionCorrectionPackage package, CancellationToken cancellationToken)
    {
        await _correctionReviewGate.WaitAsync(cancellationToken);
        try
        {
            var requestedCount = package.Corrections?.Count ?? 0;
            _logger.Info($"CorrectionPush recebido. ReportId='{package.ReportId}'; " +
                         $"Correções={requestedCount}.");
            RestoreFromTray();

            if (_closingAfterSave || _finalizationInProgress)
                return CorrectionFailure(package, "O GeoAutoDocs está finalizando o projeto.");
            if (_currentProject is null)
            {
                MessageBox.Show("Foram recebidas correções do GeoAutoEngine, mas nenhum projeto está aberto.",
                    "Correções do GeoAutoEngine", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CorrectionFailure(package, "Nenhum projeto está aberto no GeoAutoDocs.");
            }
            if (IsReadOnly)
            {
                MessageBox.Show("Foram recebidas correções, mas o projeto está aberto somente para leitura.",
                    "Correções do GeoAutoEngine", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CorrectionFailure(package, "O projeto está aberto somente para leitura.");
            }

            InspectionCorrectionPlan plan;
            try
            {
                plan = _correctionService.Prepare(_currentProject, package);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                _logger.Error("Pacote de correções rejeitado durante a validação.", ex);
                MessageBox.Show(ex.Message, "Correções do GeoAutoEngine",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return CorrectionFailure(package, ex.Message);
            }

            if (plan.Items.Count == 0)
            {
                MessageBox.Show("O GeoAutoEngine enviou um pacote sem ocorrências alteradas.",
                    "Correções do GeoAutoEngine", MessageBoxButton.OK, MessageBoxImage.Information);
                return CorrectionFailure(package, "O pacote não contém correções.");
            }

            if (plan.Items.All(x => x.Status == InspectionCorrectionResolutionStatus.Unchanged))
            {
                MessageBox.Show("As correções recebidas já estão aplicadas neste projeto.",
                    "Correções do GeoAutoEngine", MessageBoxButton.OK, MessageBoxImage.Information);
                return CorrectionAcknowledgement(plan, "Success", 0,
                    "Os valores recebidos já estavam atualizados.");
            }

            HideVideoSurfaceForOverlay();
            bool approved;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                var review = new CorrectionReviewWindow(plan.Items) { Owner = this };
                approved = review.ShowDialog() == true;
            }
            finally
            {
                RestoreVideoSurfaceAfterOverlay();
            }

            if (!approved)
                return CorrectionAcknowledgement(plan, "Failure", 0,
                    "As correções foram canceladas pelo usuário.", cancelled: true);
            if (plan.ApplicableCount == 0)
                return CorrectionAcknowledgement(plan, "Failure", 0,
                    "Nenhuma correção válida pôde ser aplicada.");

            var snapshots = _correctionService.Apply(plan);
            MarkPendingChanges();
            if (!await SavePendingAsync())
            {
                _correctionService.Rollback(snapshots);
                MarkPendingChanges();
                RefreshCorrectionAffectedViews(plan);
                _logger.Info($"CorrectionPush revertido após falha de salvamento. " +
                             $"ReportId='{package.ReportId}'; Correções={requestedCount}.");
                return CorrectionAcknowledgement(plan, "Failure", 0,
                    "As correções foram revertidas porque o projeto não pôde ser salvo.",
                    saveFailed: true);
            }

            RefreshCorrectionAffectedViews(plan);
            var status = plan.InvalidCount > 0 ? "Partial" : "Success";
            var message = status == "Partial"
                ? $"{snapshots.Count} correção(ões) aplicada(s); {plan.InvalidCount} item(ns) não foi(ram) localizado(s) ou validado(s)."
                : $"{snapshots.Count} correção(ões) aplicada(s) e salva(s).";
            _logger.Info($"Correções do GeoAutoEngine aplicadas. ReportId='{package.ReportId}'; " +
                         $"Status='{status}'; Aplicadas={snapshots.Count}; Inválidas={plan.InvalidCount}.");
            return CorrectionAcknowledgement(plan, status, snapshots.Count, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CorrectionFailure(package, "A revisão foi cancelada porque o canal foi encerrado.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.Error("Falha ao revisar ou aplicar correções do GeoAutoEngine.", ex);
            return CorrectionFailure(package, "O GeoAutoDocs falhou ao revisar ou aplicar as correções.");
        }
        finally
        {
            _correctionReviewGate.Release();
        }
    }

    private void RefreshCorrectionAffectedViews(InspectionCorrectionPlan plan)
    {
        if (_currentSection is not null &&
            plan.Items.Any(x => x.CanApply && x.Correction.SectionId == _currentSection.Id))
            RefreshOccurrences();
        ReportsList.Items.Refresh();
        RefreshReportReview();
        ScheduleOverviewRefresh();
    }

    private static InspectionCorrectionAcknowledgement CorrectionFailure(
        InspectionCorrectionPackage package, string message) => new()
    {
        Accepted = false,
        Status = "Failure",
        Message = message,
        RequestedCount = package.Corrections?.Count ?? 0,
        AppliedCount = 0,
        Results = (package.Corrections ?? []).Select(x => new InspectionCorrectionItemAcknowledgement
        {
            SectionId = x.SectionId,
            OccurrenceId = x.OccurrenceId,
            Applied = false,
            Status = "Failure",
            Message = message
        }).ToList()
    };

    private static InspectionCorrectionAcknowledgement CorrectionAcknowledgement(
        InspectionCorrectionPlan plan, string status, int appliedCount, string message,
        bool cancelled = false, bool saveFailed = false) => new()
    {
        Accepted = status is "Success" or "Partial",
        Status = status,
        Message = message,
        RequestedCount = plan.Items.Count,
        AppliedCount = appliedCount,
        Results = plan.Items.Select(item =>
        {
            var applied = status is "Success" or "Partial" && item.CanApply;
            var itemStatus = applied ? "Applied" : item.Status switch
            {
                InspectionCorrectionResolutionStatus.Unchanged => "Unchanged",
                InspectionCorrectionResolutionStatus.NotFound => "NotFound",
                InspectionCorrectionResolutionStatus.Invalid => "Invalid",
                _ when cancelled => "Cancelled",
                _ when saveFailed => "SaveFailed",
                _ => "NotApplied"
            };
            return new InspectionCorrectionItemAcknowledgement
            {
                SectionId = item.Correction.SectionId,
                OccurrenceId = item.Correction.OccurrenceId,
                Applied = applied,
                Status = itemStatus,
                Message = applied ? "Correção aplicada." :
                    cancelled ? "Cancelada pelo usuário." :
                    saveFailed && item.CanApply ? "Revertida após falha de salvamento." :
                    item.Message
            };
        }).ToList()
    };

    private async void AnomalyCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_playerDisposed || _playerDisposeInProgress) return;
        AnomalyCatalogButton.IsEnabled = false;
        HideVideoSurfaceForOverlay();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var catalog = new AnomalyCatalogWindow(_anomalyCatalogService, IsReadOnly) { Owner = this };
            catalog.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao abrir o catálogo de anomalias.", ex);
            MessageBox.Show("Não foi possível abrir o catálogo de anomalias. Consulte o log.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RestoreVideoSurfaceAfterOverlay();
            AnomalyCatalogButton.IsEnabled = true;
        }
    }

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

    private void RefreshOverviewCounters()
    {
        if (_currentProject is null) return;
        OverviewProjectName.Text = _currentProject.Name;
        OverviewClientName.Text = string.IsNullOrWhiteSpace(_currentProject.DefaultClient)
            ? "Cliente não informado" : _currentProject.DefaultClient;
        OverviewReportsCount.Text = _currentProject.Reports.Count.ToString(CultureInfo.CurrentCulture);
        OverviewSectionsCount.Text = _currentProject.Locations.Sum(x => x.Sections.Count).ToString(CultureInfo.CurrentCulture);
        OverviewOccurrencesCount.Text = _currentProject.Locations.Sum(x => x.Sections.Sum(y => y.Occurrences.Count))
            .ToString(CultureInfo.CurrentCulture);
        OverviewUpdatedAt.Text = _currentProject.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
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
        RefreshOverviewCounters();
        ScheduleOverviewRefresh();
        _loadingInspections = false;
    }

    private void RefreshSections()
    {
        RefreshSectionSelector();
        RefreshVideos();
        RefreshCaptures();
        RefreshOccurrences();
        UpdateInspectionWriteState();
        ScheduleOverviewRefresh();
    }

    private void RefreshSectionSelector()
    {
        var sectionId = _currentSection?.Id;
        SectionsList.ItemsSource = _currentLocation?.Sections.OrderBy(x => x.Order).ToList() ?? [];
        _currentSection = _currentLocation?.Sections.FirstOrDefault(x => x.Id == sectionId)
                          ?? _currentLocation?.Sections.OrderBy(x => x.Order).FirstOrDefault();
        SectionsList.SelectedItem = _currentSection;
    }

    private void RefreshSectionMetadata()
    {
        SectionsList.Items.Refresh();
        SectionsList.SelectedItem = _currentSection;
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
            ShowVideoState("Escolha um local e um trecho para continuar.", false);
        else if (items.Count == 0)
            ShowVideoState("Este trecho ainda não possui vídeos vinculados.", !IsReadOnly);
        else if (selected is null)
            ShowVideoState("Escolha um dos vídeos vinculados ao trecho.", false);
        else if (_currentMedia is null)
            ShowVideoState("O vídeo já está vinculado. Clique em “Recarregar vídeo” para exibi-lo.", false);
        UpdateInspectionWriteState();
        StartVideoAvailabilityScan(Volatile.Read(ref _inspectionLoadGeneration),
            _inspectionLoadCts?.Token ?? CancellationToken.None);
    }

    private void StartVideoAvailabilityScan(long generation, CancellationToken token)
    {
        if (VideosList.ItemsSource is not IEnumerable<VideoListItem> source) return;
        var items = source.ToList();
        if (items.Count == 0) return;
        _ = UpdateVideoAvailabilityAsync(items, generation, token);
    }

    private async Task UpdateVideoAvailabilityAsync(IReadOnlyCollection<VideoListItem> items, long generation,
        CancellationToken token)
    {
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            }, async (item, cancellationToken) =>
            {
                bool available;
                try { available = File.Exists(item.Video.FilePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    available = false;
                    _logger.Error($"Falha ao verificar disponibilidade do video. Caminho='{item.Video.FilePath}'.", ex);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (generation != Volatile.Read(ref _inspectionLoadGeneration) ||
                        _currentSection?.Id != item.Video.InspectionSectionId ||
                        VideosList.ItemsSource is not IEnumerable<VideoListItem> currentItems ||
                        !currentItems.Contains(item)) return;
                    item.SetAvailability(available);
                    UpdateInspectionWriteState();
                    if (_currentVideo?.Id == item.Id && _currentMedia is null)
                        ShowVideoState(available
                            ? "O vídeo já está vinculado. Clique em “Recarregar vídeo” para exibi-lo."
                            : "O arquivo está ausente, bloqueado ou ainda indisponível no Dropbox.", false,
                            available ? VideoSurfaceState.Empty : VideoSurfaceState.Missing);
                }, DispatcherPriority.Background, cancellationToken);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.Info($"Verificacao de disponibilidade de videos cancelada. Geracao={generation}.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao atualizar disponibilidade da lista de videos. Geracao={generation}.", ex);
        }
    }

    private void RefreshCaptures()
    {
        var count = _currentProject is null || _currentSection is null
            ? 0
            : _currentProject.Captures.Count(x =>
                x.SectionId == _currentSection.Id && x.Status == CaptureStatus.Pending);
        PendingCapturesCountText.Text = count.ToString();
        PendingCapturesButton.ToolTip = count switch
        {
            0 => "Nenhuma captura pendente neste trecho",
            1 => "Abrir 1 captura pendente",
            _ => $"Abrir {count} capturas pendentes"
        };
        PendingCapturesButton.IsEnabled = _currentProject is not null && _currentSection is not null && count > 0;
        if (_trayPendingCapturesItem is not null)
        {
            _trayPendingCapturesItem.Text = count switch
            {
                0 => "Capturas pendentes",
                1 => "Capturas pendentes (1)",
                _ => $"Capturas pendentes ({count})"
            };
            _trayPendingCapturesItem.Enabled = count > 0;
        }
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
            UpdateSectionReviewState();
            return;
        }
        var canModify = !IsReadOnly && _currentSection.AnomalyReviewStatus != SectionAnomalyReviewStatus.Completed;
        var modifyToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : IsSectionReviewLocked ? "Reabra o trecho para editar" : "Arraste para reordenar";
        var all = _currentSection.Occurrences
            .OrderBy(x => x.Order).ThenBy(x => x.CreatedAt)
            .Select(x => new OccurrenceListItem(x, canModify, modifyToolTip)).ToList();
        ApplyOccurrenceItems(all);
    }

    private async Task RefreshOccurrencesAsync(CancellationToken cancellationToken, long generation)
    {
        var section = _currentSection;
        if (section is null)
        {
            RefreshOccurrences();
            return;
        }
        var canModify = !IsReadOnly && section.AnomalyReviewStatus != SectionAnomalyReviewStatus.Completed;
        var modifyToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : IsSectionReviewLocked ? "Reabra o trecho para editar" : "Arraste para reordenar";
        var snapshot = section.Occurrences.OrderBy(x => x.Order).ThenBy(x => x.CreatedAt).ToList();
        var all = await Task.Run(() => snapshot
            .Select(x => new OccurrenceListItem(x, canModify, modifyToolTip)).ToList(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _inspectionLoadGeneration) || _currentSection?.Id != section.Id) return;
        ApplyOccurrenceItems(all);
    }

    private void ApplyOccurrenceItems(List<OccurrenceListItem> all)
    {
        var unfilteredCount = all.Count;
        var incomplete = all.Count(x => !x.IsComplete);
        AnomalySummaryText.Text = $"{all.Count} apontamento(s) • {all.Sum(x => x.EvidenceCount)} imagem(ns) • {incomplete} pendência(s)";
        var query = AnomalyListSearchBox?.Text.Trim() ?? "";
        if (query.Length > 0)
            all = all.Where(x => x.Location.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 x.Anomalies.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 x.PositionSummary.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        OccurrencesList.ItemsSource = all;
        OccurrencesEmptyTitle.Text = unfilteredCount == 0
            ? "Nenhuma anomalia registrada neste trecho."
            : "Nenhuma anomalia encontrada.";
        OccurrencesEmptyDescription.Text = unfilteredCount == 0
            ? "Durante a inspeção, capture um frame para começar."
            : "Ajuste a pesquisa para ver outros registros.";
        OccurrencesEmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSectionReviewState();
    }

    private void ScheduleOverviewRefresh()
    {
        _overviewRefreshTimer.Stop();
        _overviewRefreshTimer.Start();
    }

    private bool IsSectionReviewLocked =>
        _currentSection?.AnomalyReviewStatus == SectionAnomalyReviewStatus.Completed;

    private void UpdateSectionReviewState()
    {
        if (_currentSection is null)
        {
            SectionReviewStatusText.Text = "Selecione um trecho";
            SectionPersistenceStatusText.Text = string.Empty;
            CompleteSectionAnomaliesButton.Content = "Concluir anomalias do trecho";
            CompleteSectionAnomaliesButton.IsEnabled = false;
            return;
        }
        SectionReviewStatusText.Text = _currentSection.AnomalyReviewStatus switch
        {
            SectionAnomalyReviewStatus.Completed => "Concluído",
            SectionAnomalyReviewStatus.Reviewed => "Revisado • edição aberta",
            _ => "Em edição"
        };
        SectionPersistenceStatusText.Text = _saveErrorNotified ? "Falha no salvamento"
            : _hasPendingChanges ? "Salvamento pendente"
            : _currentSection.AnomalyReviewUpdatedAt is DateTime updated
                ? $"Persistido em {updated.ToLocalTime():dd/MM/yyyy HH:mm}" : "Alterações salvas automaticamente";
        CompleteSectionAnomaliesButton.Content = IsSectionReviewLocked
            ? "Reabrir para edição" : "Concluir anomalias do trecho";
        CompleteSectionAnomaliesButton.IsEnabled = !IsReadOnly && !_sectionReviewInProgress;
    }

    private async void CompleteSectionAnomalies_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || IsReadOnly || _sectionReviewInProgress) return;
        _sectionReviewInProgress = true;
        UpdateSectionReviewState();
        try
        {
            if (_hasPendingChanges && !await SavePendingAsync()) return;
            if (IsSectionReviewLocked)
            {
                if (MessageBox.Show("Reabrir as anomalias deste trecho para edição?", "Reabrir trecho",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (await PersistSectionReviewStateAsync(SectionAnomalyReviewStatus.Reviewed))
                    MessageBox.Show("Trecho reaberto para edição.", "GeoAutoDocs", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                return;
            }

            var pending = SectionReviewRules.GetPending(_currentProject, _currentSection);
            if (pending.HasPending)
            {
                var details = new List<string>();
                if (pending.IncompleteOccurrences > 0)
                    details.Add($"{pending.IncompleteOccurrences} anomalia(s) incompleta(s)");
                if (pending.PendingCaptures > 0)
                    details.Add($"{pending.PendingCaptures} captura(s) pendente(s)");
                if (MessageBox.Show($"Existem {string.Join(" e ", details)}. Concluir mesmo assim?",
                        "Pendências do trecho", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            if (await PersistSectionReviewStateAsync(SectionAnomalyReviewStatus.Completed))
                MessageBox.Show("Anomalias do trecho concluídas e persistidas.", "GeoAutoDocs",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _sectionReviewInProgress = false;
            RefreshOccurrences();
            UpdateInspectionWriteState();
        }
    }

    private async Task<bool> PersistSectionReviewStateAsync(SectionAnomalyReviewStatus status)
    {
        if (_currentSection is null) return false;
        var previousStatus = _currentSection.AnomalyReviewStatus;
        var previousUpdatedAt = _currentSection.AnomalyReviewUpdatedAt;
        _currentSection.AnomalyReviewStatus = status;
        _currentSection.AnomalyReviewUpdatedAt = DateTime.UtcNow;
        MarkPendingChanges();
        SectionPersistenceStatusText.Text = "Salvando...";
        if (await SavePendingAsync()) return true;
        _currentSection.AnomalyReviewStatus = previousStatus;
        _currentSection.AnomalyReviewUpdatedAt = previousUpdatedAt;
        SectionPersistenceStatusText.Text = "Falha no salvamento • conclusão não aplicada";
        return false;
    }

    private void OccurrencesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OccurrenceListItem { CanModify: true } item }) return;
        _occurrenceDragStart = e.GetPosition(OccurrencesList);
        _occurrenceDragId = item.Id;
    }

    private void OccurrencesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _occurrenceDragId = null;
            return;
        }
        if (_occurrenceDragId is not Guid id || IsReadOnly || IsSectionReviewLocked) return;
        var position = e.GetPosition(OccurrencesList);
        if (Math.Abs(position.X - _occurrenceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _occurrenceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _occurrenceDragId = null;
        DragDrop.DoDragDrop(OccurrencesList, id, DragDropEffects.Move);
    }

    private async void OccurrencesList_Drop(object sender, DragEventArgs e)
    {
        if (_currentSection is null || IsReadOnly || IsSectionReviewLocked || !e.Data.GetDataPresent(typeof(Guid))) return;
        var sourceId = (Guid)e.Data.GetData(typeof(Guid));
        var ordered = _currentSection.Occurrences.OrderBy(x => x.Order).ThenBy(x => x.CreatedAt).ToList();
        var source = ordered.FirstOrDefault(x => x.Id == sourceId);
        if (source is null) return;
        var previousOrders = ordered.ToDictionary(x => x.Id, x => x.Order);
        var targetContainer = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        var target = targetContainer?.DataContext as OccurrenceListItem;
        if (target?.Id == sourceId) return;
        ordered.Remove(source);
        var targetIndex = target is null ? ordered.Count : ordered.FindIndex(x => x.Id == target.Id);
        if (targetIndex < 0) return;
        if (targetContainer is not null && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
            targetIndex++;
        ordered.Insert(targetIndex, source);
        for (var index = 0; index < ordered.Count; index++) ordered[index].Order = index;
        MarkPendingChanges();
        UpdateSectionReviewState();
        if (!await SavePendingAsync())
            foreach (var occurrence in ordered) occurrence.Order = previousOrders[occurrence.Id];
        else if (_sharedSession is not null) _sharedDirty = true;
        RefreshOccurrences();
        e.Handled = true;
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
        if (AnomalySearchPlaceholder is not null)
            AnomalySearchPlaceholder.Visibility = string.IsNullOrEmpty(AnomalyListSearchBox?.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        if (IsInitialized) RefreshOccurrences();
    }

    private void OccurrenceActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id, DataContext: OccurrenceListItem item } button) return;
        var menuStyle = (Style)FindResource("CleanContextMenuStyle");
        var itemStyle = (Style)FindResource("CleanContextMenuItemStyle");
        var separatorStyle = (Style)FindResource("CleanContextMenuSeparatorStyle");
        var menu = new ContextMenu { PlacementTarget = button, Style = menuStyle };
        var goToVideo = new MenuItem
        {
            Header = "Ir ao vídeo", Tag = id, IsEnabled = item.CanGoToVideo, Style = itemStyle,
            ToolTip = item.CanGoToVideo ? "Abrir o vídeo no instante da evidência" : "Nenhuma evidência vinculada a vídeo."
        };
        ToolTipService.SetShowOnDisabled(goToVideo, true);
        goToVideo.Click += GoToOccurrenceVideo_Click;
        var duplicate = new MenuItem
        {
            Header = "Duplicar", Tag = id, IsEnabled = item.CanModify, Style = itemStyle,
            ToolTip = item.CanModify ? "Criar uma cópia editável" : item.ModifyToolTip
        };
        ToolTipService.SetShowOnDisabled(duplicate, true);
        duplicate.Click += DuplicateOccurrence_Click;
        var delete = new MenuItem
        {
            Header = "Excluir", Tag = id, IsEnabled = item.CanModify, Style = itemStyle,
            Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"),
            ToolTip = item.CanModify ? "Excluir a anomalia" : item.ModifyToolTip
        };
        ToolTipService.SetShowOnDisabled(delete, true);
        delete.Click += DeleteOccurrence_Click;
        menu.Items.Add(goToVideo);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(delete);
        if (IsSectionReviewLocked)
        {
            var reopen = new MenuItem
            {
                Header = "Reabrir para edição", IsEnabled = !IsReadOnly, Style = itemStyle,
                ToolTip = IsReadOnly ? "Projeto aberto somente para leitura." : "Reabrir o trecho e habilitar as ações"
            };
            ToolTipService.SetShowOnDisabled(reopen, true);
            reopen.Click += CompleteSectionAnomalies_Click;
            menu.Items.Add(new Separator { Style = separatorStyle });
            menu.Items.Add(reopen);
        }
        menu.IsOpen = true;
    }

    private void UpdateInspectionWriteState()
    {
        var selectedVideoAvailability = (VideosList.SelectedItem as VideoListItem)?.IsAvailable;
        LocationsList.IsEnabled = _currentProject is not null && !_captureInProgress;
        SectionsList.IsEnabled = _currentLocation is not null && !_captureInProgress;
        VideosList.IsEnabled = _currentSection is not null && !_captureInProgress;
        AddLocationButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentProject is not null;
        AddSectionButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentLocation is not null;
        RenameLocationButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentLocation is not null;
        DeleteLocationButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentLocation is not null;
        ReorderLocationsButton.IsEnabled = !IsReadOnly && !_captureInProgress &&
                                            _currentProject?.Locations.Count > 1;
        ReorderLocationsButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : _currentProject?.Locations.Count > 1 ? "Ordenar locais arrastando para cima ou para baixo"
            : "Crie pelo menos dois locais para alterar a ordem.";
        ReorderSectionsButton.IsEnabled = !IsReadOnly && !_captureInProgress &&
                                          _currentLocation?.Sections.Count > 1;
        ReorderSectionsButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : _currentLocation?.Sections.Count > 1 ? "Ordenar trechos arrastando para cima ou para baixo"
            : "Crie pelo menos dois trechos para alterar a ordem.";
        var hasProjectSections = _currentProject?.Locations.Any(x => x.Sections.Count > 0) == true;
        RenameSectionsButton.IsEnabled = !IsReadOnly && !_captureInProgress && hasProjectSections;
        RenameSectionsButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : hasProjectSections ? "Renomear todos os trechos em sequência"
            : "Crie pelo menos um trecho para usar a renomeação automática.";
        EditSectionButton.IsEnabled = !IsReadOnly && !IsSectionReviewLocked && !_captureInProgress && _currentSection is not null;
        EditSectionButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : IsSectionReviewLocked ? "Reabra o trecho para editar"
            : _currentSection is null ? "Selecione um trecho para editar." : "Editar trecho";
        DeleteSectionButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentSection is not null;
        AddVideoButton.IsEnabled = !IsReadOnly && !_captureInProgress && !_videoLoadInProgress && _currentSection is not null;
        AddVideoButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : _currentSection is null ? "Selecione ou crie um trecho antes de adicionar um vídeo."
            : "Adicionar vídeo ao trecho selecionado";
        EmptyVideoButton.IsEnabled = AddVideoButton.IsEnabled;
        ReloadVideoButton.IsEnabled = !_captureInProgress && !_playerOperationInProgress && !_videoLoadInProgress &&
                                      _currentVideo is not null && selectedVideoAvailability == true;
        CaptureFrameButton.IsEnabled = !_captureInProgress && !IsReadOnly && !IsSectionReviewLocked &&
                                       _currentVideo is not null && _currentMedia is not null;
        CaptureFrameOverlayButton.IsEnabled = CaptureFrameButton.IsEnabled;
        RelocateVideoButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentVideo is not null &&
                                        selectedVideoAvailability == false;
        RelocateVideoButton.Visibility = _currentVideo is not null && selectedVideoAvailability == false
            ? Visibility.Visible : Visibility.Collapsed;
        RemoveVideoButton.IsEnabled = !IsReadOnly && !_captureInProgress && _currentVideo is not null;
        NewOccurrenceButton.IsEnabled = !IsReadOnly && !IsSectionReviewLocked && !_captureInProgress && _currentSection is not null;
        NewOccurrenceButton.ToolTip = IsReadOnly ? "Projeto aberto somente para leitura."
            : IsSectionReviewLocked ? "Reabra o trecho para editar"
            : _currentSection is null ? "Selecione um trecho para cadastrar uma anomalia." : "Cadastrar anomalia";
        UpdateSectionReviewState();
    }

    private async void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || IsReadOnly) return;
        HideVideoSurfaceForOverlay();
        string? name;
        try { name = NewLocationWindow.Show(this); }
        finally { RestoreVideoSurfaceAfterOverlay(); }
        if (name is null) return;

        var location = new Location
        {
            ProjectId = _currentProject.Id,
            Name = name,
            Order = NextOrder(_currentProject.Locations.Select(x => x.Order))
        };
        _currentProject.Locations.Add(location);
        _currentLocation = location;
        RefreshInspectionTree();
        await SaveInspectionChangesAsync();
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
        var location = new Location { ProjectId = _currentProject.Id, Name = name, Order = NextOrder(_currentProject.Locations.Select(x => x.Order)) };
        _currentProject.Locations.Add(location);
        _currentLocation = location;
        CloseInspectionForm();
        RefreshInspectionTree();
        await SaveInspectionChangesAsync();
    }

    private async void AddSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentLocation is null || IsReadOnly) return;
        HideVideoSurfaceForOverlay();
        NewSectionDraft? draft;
        try { draft = NewSectionWindow.Show(this); }
        finally { RestoreVideoSurfaceAfterOverlay(); }
        if (draft is null) return;

        var section = new InspectionSection
        {
            LocationId = _currentLocation.Id,
            Name = draft.Name,
            Description = draft.Description,
            Diameter = draft.Diameter,
            StartPoint = draft.StartPoint,
            EndPoint = draft.EndPoint,
            Reference = draft.Reference,
            InvestigatedMeters = draft.InvestigatedMeters,
            Order = NextOrder(_currentLocation.Sections.Select(x => x.Order))
        };
        _currentLocation.Sections.Add(section);
        _currentSection = section;
        RefreshSections();
        await SaveInspectionChangesAsync();
    }

    private async void RenameLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLocation is null || IsReadOnly) return;
        var name = TextPromptWindow.Ask(this, "Renomear local", "Nome do local", _currentLocation.Name);
        if (string.IsNullOrWhiteSpace(name) || name == _currentLocation.Name) return;
        _currentLocation.Name = name;
        RefreshInspectionTree();
        await SaveInspectionChangesAsync();
    }

    private async void DeleteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentLocation is null || IsReadOnly) return;
        var location = _currentLocation;
        if (MessageBox.Show($"Excluir o local '{location.Name}' e todos os seus trechos, ocorrências e capturas?",
                "Excluir local", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await StopAndReleaseCurrentMediaAsync();
        var sectionIds = location.Sections.Select(x => x.Id).ToHashSet();
        _currentProject.Captures.RemoveAll(x => sectionIds.Contains(x.SectionId));
        foreach (var report in _currentProject.Reports)
            report.SectionLinks.RemoveAll(x => sectionIds.Contains(x.SectionId));
        _currentProject.Locations.Remove(location);
        _currentLocation = null;
        _currentSection = null;
        _currentVideo = null;
        RefreshInspectionTree();
        await SaveInspectionChangesAsync();
    }

    private async void RenameSectionsAutomatically_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RenameSectionsAutomaticallyAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha inesperada na renomeação automática dos trechos.", ex);
            ShowError("Não foi possível renomear os trechos.", ex);
        }
    }

    private async Task RenameSectionsAutomaticallyAsync()
    {
        if (_currentProject is null || IsReadOnly) return;

        var project = _currentProject;
        var selectedLocationId = _currentLocation?.Id;
        var selectedSectionId = _currentSection?.Id;
        IReadOnlyList<SectionRenamePlanItem>? requestedPlan = null;

        HideVideoSurfaceForOverlay();
        try
        {
            var dialog = new SectionAutomaticNamingWindow(project) { Owner = this };
            if (dialog.ShowDialog() == true) requestedPlan = dialog.RenamePlan;
        }
        finally
        {
            RestoreVideoSurfaceAfterOverlay();
        }

        if (requestedPlan is null) return;

        var sectionsById = project.Locations
            .SelectMany(location => location.Sections)
            .ToDictionary(section => section.Id);
        if (requestedPlan.Count != sectionsById.Count ||
            requestedPlan.Select(item => item.SectionId).Distinct().Count() != requestedPlan.Count ||
            requestedPlan.Any(item => !sectionsById.ContainsKey(item.SectionId)))
            throw new InvalidDataException("A prévia de renomeação não corresponde aos trechos atuais do projeto.");

        var previousNames = sectionsById.ToDictionary(pair => pair.Key, pair => pair.Value.Name);
        if (requestedPlan.All(item =>
                string.Equals(sectionsById[item.SectionId].Name, item.ProposedName, StringComparison.Ordinal)))
            return;

        try
        {
            foreach (var item in requestedPlan)
            {
                if (string.IsNullOrWhiteSpace(item.ProposedName))
                    throw new InvalidDataException($"O novo nome do trecho '{item.SectionId}' está vazio.");
                sectionsById[item.SectionId].Name = item.ProposedName;
            }

            MarkPendingChanges();
            SaveStatus.Text = "Salvando novos nomes dos trechos...";
            if (!await SavePendingAsync())
            {
                foreach (var pair in previousNames)
                    sectionsById[pair.Key].Name = pair.Value;
                _logger.Info($"Renomeação automática revertida após falha de salvamento. ProjectId='{project.Id}'.");
            }
            else
            {
                _logger.Info(
                    $"Trechos renomeados automaticamente. ProjectId='{project.Id}'; Quantidade='{requestedPlan.Count}'; " +
                    $"PrimeiroNome='{requestedPlan.FirstOrDefault()?.ProposedName}'; UltimoNome='{requestedPlan.LastOrDefault()?.ProposedName}'.");
            }
        }
        catch
        {
            foreach (var pair in previousNames)
                sectionsById[pair.Key].Name = pair.Value;
            throw;
        }
        finally
        {
            if (_currentProject?.Id == project.Id)
            {
                _loadingInspections = true;
                try
                {
                    _currentLocation = project.Locations.FirstOrDefault(x => x.Id == selectedLocationId)
                                       ?? project.Locations.OrderBy(x => x.Order).FirstOrDefault();
                    _currentSection = _currentLocation?.Sections.FirstOrDefault(x => x.Id == selectedSectionId)
                                      ?? _currentLocation?.Sections.OrderBy(x => x.Order).FirstOrDefault();
                    LocationsList.Items.Refresh();
                    RefreshSectionSelector();
                    RefreshReportSectionSelection();
                    RefreshReportReview();
                    ScheduleOverviewRefresh();
                }
                finally
                {
                    _loadingInspections = false;
                    UpdateInspectionWriteState();
                }
            }
        }
    }

    private async void ReorderLocations_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || IsReadOnly || _currentProject.Locations.Count < 2) return;

        var project = _currentProject;
        var selectedLocationId = _currentLocation?.Id;
        var selectedSectionId = _currentSection?.Id;
        IReadOnlyList<Guid>? requestedOrder = null;
        HideVideoSurfaceForOverlay();
        try
        {
            var dialog = new LocationOrderWindow(project.Locations) { Owner = this };
            if (dialog.ShowDialog() == true) requestedOrder = dialog.OrderedLocationIds;
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao abrir a ordenação de locais. ProjectId='{project.Id}'.", ex);
            ShowError("Não foi possível abrir a ordenação de locais.", ex);
        }
        finally
        {
            RestoreVideoSurfaceAfterOverlay();
        }

        if (requestedOrder is null) return;
        var previousOrders = project.Locations.ToDictionary(x => x.Id, x => x.Order);
        var previousReportOrders = project.Reports
            .SelectMany(report => report.SectionLinks.Select(link =>
                (Key: (report.Id, link.SectionId), link.Order)))
            .ToDictionary(x => x.Key, x => x.Order);
        var currentIds = project.Locations.OrderBy(x => x.Order).Select(x => x.Id).ToList();
        if (currentIds.SequenceEqual(requestedOrder)) return;

        try
        {
            LocationOrdering.Apply(project, requestedOrder);
            NormalizeAllReportSectionOrders(project);
            MarkPendingChanges();
            SaveStatus.Text = "Salvando nova ordem dos locais...";
            if (!await SavePendingAsync())
            {
                RestoreLocationAndReportOrders(project, previousOrders, previousReportOrders);
                MarkPendingChanges();
                _logger.Info($"Reordenação de locais revertida após falha de salvamento. ProjectId='{project.Id}'.");
            }
            else
            {
                _logger.Info($"Locais reordenados. ProjectId='{project.Id}'; Ordem='{string.Join(",", requestedOrder)}'.");
            }
        }
        catch (Exception ex)
        {
            RestoreLocationAndReportOrders(project, previousOrders, previousReportOrders);
            _logger.Error($"Falha ao aplicar a ordenação de locais. ProjectId='{project.Id}'.", ex);
            ShowError("Não foi possível alterar a ordem dos locais.", ex);
        }
        finally
        {
            if (_currentProject?.Id == project.Id)
            {
                _currentLocation = project.Locations.FirstOrDefault(x => x.Id == selectedLocationId)
                                   ?? project.Locations.OrderBy(x => x.Order).FirstOrDefault();
                _currentSection = _currentLocation?.Sections.FirstOrDefault(x => x.Id == selectedSectionId)
                                  ?? _currentLocation?.Sections.OrderBy(x => x.Order).FirstOrDefault();
                RefreshInspectionTree();
                RefreshReportReview();
            }
        }
    }

    private static void NormalizeAllReportSectionOrders(Project project)
    {
        var order = project.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name)
            .SelectMany(x => x.Sections.OrderBy(y => y.Order).ThenBy(y => y.Name))
            .Select((section, index) => (section.Id, index))
            .ToDictionary(x => x.Id, x => x.index);
        foreach (var report in project.Reports)
        foreach (var link in report.SectionLinks)
            link.Order = order.GetValueOrDefault(link.SectionId, int.MaxValue);
    }

    private static void RestoreLocationAndReportOrders(
        Project project,
        IReadOnlyDictionary<Guid, int> locationOrders,
        IReadOnlyDictionary<(Guid ReportId, Guid SectionId), int> reportOrders)
    {
        foreach (var location in project.Locations)
            if (locationOrders.TryGetValue(location.Id, out var order)) location.Order = order;
        foreach (var report in project.Reports)
        foreach (var link in report.SectionLinks)
            if (reportOrders.TryGetValue((report.Id, link.SectionId), out var order)) link.Order = order;
    }

    private async void ReorderSections_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLocation is null || IsReadOnly || _currentLocation.Sections.Count < 2) return;

        var location = _currentLocation;
        var selectedId = _currentSection?.Id;
        IReadOnlyList<Guid>? requestedOrder = null;
        HideVideoSurfaceForOverlay();
        try
        {
            var dialog = new SectionOrderWindow(location.Sections) { Owner = this };
            if (dialog.ShowDialog() == true) requestedOrder = dialog.OrderedSectionIds;
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao abrir a ordenação de trechos. LocalId='{location.Id}'.", ex);
            ShowError("Não foi possível abrir a ordenação de trechos.", ex);
        }
        finally
        {
            RestoreVideoSurfaceAfterOverlay();
        }

        if (requestedOrder is null) return;
        var previousOrder = location.Sections.ToDictionary(x => x.Id, x => x.Order);
        var previousIds = location.Sections.OrderBy(x => x.Order).Select(x => x.Id).ToList();
        if (previousIds.SequenceEqual(requestedOrder)) return;

        try
        {
            var sectionsById = location.Sections.ToDictionary(x => x.Id);
            if (requestedOrder.Count != sectionsById.Count ||
                requestedOrder.Any(id => !sectionsById.ContainsKey(id)) ||
                requestedOrder.Distinct().Count() != requestedOrder.Count)
                throw new InvalidDataException("A ordenação retornada não corresponde aos trechos do Local.");

            for (var index = 0; index < requestedOrder.Count; index++)
                sectionsById[requestedOrder[index]].Order = index;

            MarkPendingChanges();
            SaveStatus.Text = "Salvando nova ordem dos trechos...";
            if (!await SavePendingAsync())
            {
                foreach (var section in location.Sections)
                    section.Order = previousOrder[section.Id];
                _logger.Info($"Reordenação de trechos revertida após falha de salvamento. LocalId='{location.Id}'.");
            }
            else
            {
                _logger.Info($"Trechos reordenados. LocalId='{location.Id}'; Ordem='{string.Join(",", requestedOrder)}'.");
            }

            if (_currentLocation?.Id == location.Id)
            {
                _currentSection = location.Sections.FirstOrDefault(x => x.Id == selectedId)
                                  ?? location.Sections.OrderBy(x => x.Order).FirstOrDefault();
                _loadingInspections = true;
                try
                {
                    RefreshSectionSelector();
                    RefreshReportSectionSelection();
                }
                finally
                {
                    _loadingInspections = false;
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var section in location.Sections)
                if (previousOrder.TryGetValue(section.Id, out var order)) section.Order = order;
            _logger.Error($"Falha ao aplicar a ordenação de trechos. LocalId='{location.Id}'.", ex);
            ShowError("Não foi possível alterar a ordem dos trechos.", ex);
            if (_currentLocation?.Id == location.Id)
            {
                _loadingInspections = true;
                try { RefreshSectionSelector(); }
                finally { _loadingInspections = false; }
            }
        }
    }

    private async void EditSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection is null || IsReadOnly) return;
        if (IsSectionReviewLocked)
        {
            MessageBox.Show("Reabra o trecho para editar.", "Trecho concluído", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        HideVideoSurfaceForOverlay();
        NewSectionDraft? draft;
        try { draft = NewSectionWindow.Show(this, _currentSection); }
        finally { RestoreVideoSurfaceAfterOverlay(); }
        if (draft is null) return;

        _currentSection.Name = draft.Name;
        _currentSection.Description = draft.Description;
        _currentSection.Diameter = draft.Diameter;
        _currentSection.StartPoint = draft.StartPoint;
        _currentSection.EndPoint = draft.EndPoint;
        _currentSection.Reference = draft.Reference;
        _currentSection.InvestigatedMeters = draft.InvestigatedMeters;
        RefreshSectionMetadata();
        RefreshReportSectionSelection();
        await SaveSectionMetadataAsync(_currentSection);
    }

    private async void DeleteSection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentLocation is null || _currentSection is null || IsReadOnly) return;
        var section = _currentSection;
        if (MessageBox.Show($"Excluir o trecho '{section.Name}' e todas as suas ocorrências e capturas?",
                "Excluir trecho", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await StopAndReleaseCurrentMediaAsync();
        _currentProject.Captures.RemoveAll(x => x.SectionId == section.Id);
        foreach (var report in _currentProject.Reports)
            report.SectionLinks.RemoveAll(x => x.SectionId == section.Id);
        _currentLocation.Sections.Remove(section);
        _currentSection = null;
        _currentVideo = null;
        RefreshSections();
        RefreshReportSectionSelection();
        await SaveInspectionChangesAsync();
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
            Order = NextOrder(_currentLocation.Sections.Select(x => x.Order))
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

    private async void LocationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspections) return;
        var location = LocationsList.SelectedItem as Location;
        try
        {
            await ChangeInspectionContextAsync(location, null, true);
        }
        catch (Exception ex)
        {
            HandleInspectionContextFailure("troca de Local", ex);
        }
    }

    private async void SectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspections) return;
        var section = SectionsList.SelectedItem as InspectionSection;
        try
        {
            await ChangeInspectionContextAsync(_currentLocation, section, false);
        }
        catch (Exception ex)
        {
            HandleInspectionContextFailure("troca de trecho", ex);
        }
    }

    private async Task ChangeInspectionContextAsync(Location? location, InspectionSection? section,
        bool locationChanged)
    {
        var generation = Interlocked.Increment(ref _inspectionLoadGeneration);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _inspectionLoadCts, cancellation);
        previous?.Cancel();
        var token = cancellation.Token;
        var timings = new Dictionary<string, long>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();
        var locationName = location?.Name ?? "<nenhum>";
        var locationId = location?.Id.ToString() ?? "<nenhum>";
        _logger.Info($"Troca de contexto iniciada. Geracao={generation}; LocalId='{locationId}'; Local='{locationName}'; " +
                     $"Trechos={location?.Sections.Count ?? 0}; MemoriaGerenciada={GC.GetTotalMemory(false)}; WorkingSet={Environment.WorkingSet}.");

        try
        {
            DiscardCaptureDraft();
            await MeasureInspectionStageAsync("StopAndReleaseCurrentMedia", generation, timings,
                () => StopAndReleaseCurrentMediaAsync(token));
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _inspectionLoadGeneration)) return;

            _loadingInspections = true;
            try
            {
                _currentLocation = location;
                _currentSection = locationChanged ? null : section;
                _currentVideo = null;
                await MeasureInspectionStageAsync("RefreshSections", generation, timings, () =>
                {
                    RefreshSectionSelector();
                    return Task.CompletedTask;
                });
            }
            finally
            {
                _loadingInspections = false;
            }

            await MeasureInspectionStageAsync("RefreshVideos", generation, timings, () =>
            {
                _loadingInspections = true;
                try { RefreshVideos(); }
                finally { _loadingInspections = false; }
                return Task.CompletedTask;
            });
            await MeasureInspectionStageAsync("RefreshCaptures", generation, timings, () =>
            {
                RefreshCaptures();
                return Task.CompletedTask;
            });
            await MeasureInspectionStageAsync("RefreshOccurrences", generation, timings,
                () => RefreshOccurrencesAsync(token, generation));
            await MeasureInspectionStageAsync("RefreshOverviewCounters", generation, timings, () =>
            {
                RefreshOverviewCounters();
                ScheduleOverviewRefresh();
                return Task.CompletedTask;
            });

            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _inspectionLoadGeneration)) return;
            UpdateInspectionWriteState();
            var videos = _currentSection?.VideoReferences.Count ?? 0;
            var occurrences = _currentSection?.Occurrences.Count ?? 0;
            var evidence = _currentSection?.Occurrences.Sum(x => x.Evidence.Count) ?? 0;
            var captures = _currentProject?.Captures.Count(x => x.SectionId == _currentSection?.Id &&
                                                                 x.Status == CaptureStatus.Pending) ?? 0;
            var summary = string.Join("; ", timings.Select(x => $"{x.Key}={x.Value}ms"));
            _logger.Info($"Troca de contexto concluida. Geracao={generation}; LocalId='{locationId}'; Local='{locationName}'; " +
                         $"TrechoId='{_currentSection?.Id}'; Videos={videos}; Ocorrencias={occurrences}; Evidencias={evidence}; " +
                         $"CapturasPendentes={captures}; Total={total.ElapsedMilliseconds}ms; {summary}; " +
                         $"MemoriaGerenciada={GC.GetTotalMemory(false)}; WorkingSet={Environment.WorkingSet}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.Info($"Troca de contexto cancelada. Geracao={generation}; LocalId='{locationId}'; Etapa='{_inspectionLoadStage}'.");
        }
        catch (OutOfMemoryException ex)
        {
            _logger.Error($"Falta de memoria durante troca de contexto. Geracao={generation}; LocalId='{locationId}'; " +
                          $"Etapa='{_inspectionLoadStage}'; MemoriaGerenciada={GC.GetTotalMemory(false)}; WorkingSet={Environment.WorkingSet}.", ex);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha durante troca de contexto. Geracao={generation}; LocalId='{locationId}'; " +
                          $"Etapa='{_inspectionLoadStage}'; MemoriaGerenciada={GC.GetTotalMemory(false)}; WorkingSet={Environment.WorkingSet}.", ex);
            throw;
        }
        finally
        {
            if (!ReferenceEquals(Volatile.Read(ref _inspectionLoadCts), cancellation)) cancellation.Dispose();
            previous?.Dispose();
            if (generation == Volatile.Read(ref _inspectionLoadGeneration))
            {
                _inspectionLoadStage = "idle";
                _loadingInspections = false;
                UpdateInspectionWriteState();
            }
        }
    }

    private async Task MeasureInspectionStageAsync(string stage, long generation,
        IDictionary<string, long> timings, Func<Task> action)
    {
        _inspectionLoadStage = stage;
        _logger.Info($"Troca de contexto: etapa iniciada. Geracao={generation}; Etapa='{stage}'.");
        var watch = Stopwatch.StartNew();
        await action();
        timings[stage] = watch.ElapsedMilliseconds;
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private void HandleInspectionContextFailure(string operation, Exception ex)
    {
        ShowVideoState("Parte dos dados não pôde ser carregada. Selecione outro Local ou tente novamente.", false,
            VideoSurfaceState.Error);
        UpdateInspectionWriteState();
        MessageBox.Show($"Não foi possível concluir a {operation}. O projeto continua aberto e você pode tentar novamente.",
            "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        await LoadVideoAsync(_currentVideo, MediaLoadIntent.NewlyAddedVideo);
    }

    private async void VideosList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspections) return;
        _currentVideo = (VideosList.SelectedItem as VideoListItem)?.Video;
        UpdateInspectionWriteState();
        try
        {
            if (_currentVideo is null) { await StopAndReleaseCurrentMediaAsync(); return; }
            await LoadVideoAsync(_currentVideo, MediaLoadIntent.ExplicitVideoSelection);
        }
        catch (Exception ex) { ShowError("Não foi possível selecionar o vídeo.", ex); }
    }

    private void ReportEditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSidebarNavigation();

    private async Task LoadVideoAsync(VideoReference video, MediaLoadIntent intent, TimeSpan? seekTo = null)
    {
        if (!MediaLoadPolicy.CanOpen(intent))
            throw new InvalidOperationException("A seleção de Local ou trecho não pode abrir mídia automaticamente.");
        var generation = Interlocked.Increment(ref _mediaGeneration);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _inspectionLoadCts?.Token ?? CancellationToken.None);
        var previousCancellation = Interlocked.Exchange(ref _mediaLoadCts, cancellation);
        previousCancellation?.Cancel();
        var token = cancellation.Token;
        _currentVideo = video;
        _playerErrorNotified = false;
        _playerTelemetryErrorNotified = false;
        Media? pendingMedia = null;
        var lockTaken = false;
        try
        {
            var preflightWatch = Stopwatch.StartNew();
            MediaPreflightResult preflight;
            using (var preflightCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                preflightCancellation.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    preflight = await MediaPlaybackSafety.InspectAsync(video.FilePath, preflightCancellation.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    ShowVideoState("O arquivo demorou para responder. Aguarde o Dropbox concluir a sincronização e tente novamente.",
                        false, VideoSurfaceState.Error);
                    _logger.Error(
                        $"Timeout na pré-validação do vídeo. Caminho='{video.FilePath}'; Tempo={preflightWatch.ElapsedMilliseconds}ms.",
                        new TimeoutException("O cabeçalho do arquivo não ficou disponível em 15 segundos."));
                    return;
                }
            }

            if (!preflight.CanOpen)
            {
                var state = preflight.FailureKind == MediaPreflightFailureKind.Missing
                    ? VideoSurfaceState.Missing : VideoSurfaceState.Error;
                if (generation == Volatile.Read(ref _mediaGeneration))
                    ShowVideoState(preflight.FailureReason ?? "O arquivo de vídeo não pôde ser validado.", false, state);
                _logger.Error(
                    $"Vídeo rejeitado antes do LibVLC. Caminho='{video.FilePath}'; Extensao='{Path.GetExtension(video.FilePath)}'; " +
                    $"Tamanho={preflight.FileLength}; Motivo='{preflight.FailureReason}'; Tempo={preflightWatch.ElapsedMilliseconds}ms.",
                    new InvalidDataException(preflight.FailureReason));
                return;
            }

            _logger.Info(
                $"Pré-validação de vídeo concluída. Caminho='{video.FilePath}'; Extensao='{Path.GetExtension(video.FilePath)}'; " +
                $"Tamanho={preflight.FileLength}; Tempo={preflightWatch.ElapsedMilliseconds}ms; Geracao={generation}.");

            await _playerLifecycleGate.WaitAsync(token);
            lockTaken = true;
            token.ThrowIfCancellationRequested();
            if (_currentVideo?.Id != video.Id || generation != Volatile.Read(ref _mediaGeneration)) return;
            _videoLoadInProgress = true;
            UpdateInspectionWriteState();
            ShowVideoState("Preparando reprodução e controles de navegação.", false,
                VideoSurfaceState.Loading);
            pendingMedia = new Media(_libVlc, video.FilePath, FromType.FromPath);
            token.ThrowIfCancellationRequested();

            Interlocked.Exchange(ref _activeMediaGeneration, 0);
            var previousMedia = _currentMedia;
            try { _mediaPlayer.Stop(); }
            catch (Exception ex) { _logger.Error("Falha ao interromper a mídia anterior antes da troca.", ex); }
            _currentMedia = null;
            previousMedia?.Dispose();

            _currentMedia = pendingMedia;
            Interlocked.Exchange(ref _activeMediaGeneration, generation);
            if (!_mediaPlayer.Play(pendingMedia))
            {
                Interlocked.Exchange(ref _activeMediaGeneration, 0);
                _currentMedia = null;
                throw new InvalidOperationException("O player não conseguiu iniciar o vídeo.");
            }

            pendingMedia = null;
            _lastKnownPlayerTime = 0;
            _logger.Info(
                $"Vídeo entregue ao LibVLC. Caminho='{video.FilePath}'; Extensao='{Path.GetExtension(video.FilePath)}'; " +
                $"DecodificacaoHardware=False; Geracao={generation}.");
            if (seekTo is not null)
            {
                SetPlayerOperationState(true);
                try
                {
                    await WaitForMediaReadyAsync(token);
                    await SeekToDisplayedFrameAsync((long)seekTo.Value.TotalMilliseconds, token);
                }
                finally { SetPlayerOperationState(false); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.Info($"Abertura de vídeo cancelada. Geracao={generation}; Caminho='{video.FilePath}'.");
        }
        catch (Exception ex)
        {
            pendingMedia?.Dispose();
            if (generation == Volatile.Read(ref _mediaGeneration))
                ShowVideoState("Verifique o arquivo, codec ou acesso ao local de armazenamento.", false,
                    VideoSurfaceState.Error);
            _logger.Error(
                $"Falha ao abrir vídeo. Caminho='{video.FilePath}', extensão='{Path.GetExtension(video.FilePath)}', " +
                $"backend='LibVLC', decodificaçãoHardware=False, geracao={generation}.", ex);
        }
        finally
        {
            pendingMedia?.Dispose();
            if (lockTaken) _playerLifecycleGate.Release();
            Interlocked.CompareExchange(ref _mediaLoadCts, null, cancellation);
            cancellation.Dispose();
            if (generation == Volatile.Read(ref _mediaGeneration))
            {
                _videoLoadInProgress = false;
                UpdateInspectionWriteState();
            }
        }
    }

    private async void RetryVideo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentVideo is not null && !_playerOperationInProgress)
                await LoadVideoAsync(_currentVideo, MediaLoadIntent.Reload);
        }
        catch (Exception ex) { ShowError("Não foi possível recarregar o vídeo.", ex); }
    }

    private async Task WaitForMediaReadyAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 100 && _mediaPlayer.Length <= 0; attempt++)
            await Task.Delay(50, cancellationToken);
        if (_mediaPlayer.Length <= 0) throw new InvalidOperationException("Não foi possível obter a duração do vídeo.");
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null) return;
        try
        {
            if (_currentMedia is null)
            {
                await LoadVideoAsync(_currentVideo, MediaLoadIntent.Reload);
                return;
            }

            await _playerLifecycleGate.WaitAsync();
            try
            {
                if (_playerDisposed || _currentMedia is null) return;
                if (_mediaPlayer.IsPlaying) _mediaPlayer.SetPause(true);
                else if (!_mediaPlayer.Play())
                    throw new InvalidOperationException("O LibVLC não conseguiu retomar a reprodução.");
            }
            finally { _playerLifecycleGate.Release(); }
        }
        catch (Exception ex)
        {
            ShowVideoState("Não foi possível alterar o estado da reprodução. Tente recarregar o vídeo.",
                false, VideoSurfaceState.Error);
            ShowError("Falha ao reproduzir ou pausar o vídeo.", ex);
        }
    }

    private async void Back5_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(-5, false);
    private async void Back1_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(-1, false);
    private async void Forward1_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(1, true);
    private async void Forward5_Click(object sender, RoutedEventArgs e) => await SeekRelativeAsync(5, true);

    private async Task SeekRelativeAsync(int seconds, bool restorePlayback)
    {
        if (_currentMedia is null || _playerOperationInProgress) return;
        await _playerLifecycleGate.WaitAsync();
        if (_currentMedia is null)
        {
            _playerLifecycleGate.Release();
            return;
        }
        var media = _currentMedia;
        var wasPlaying = _mediaPlayer.IsPlaying;
        SetPlayerOperationState(true);
        try
        {
            await WaitForMediaReadyAsync();
            var duration = Math.Max(0, _mediaPlayer.Length);
            var ended = _mediaPlayer.State == VLCState.Ended ||
                        (duration > 0 && _lastKnownPlayerTime >= duration - 500 && _mediaPlayer.Time <= 0);
            await PauseOnDisplayedFrameAsync();
            var initial = ended ? Math.Max(0, duration - 1) : Math.Max(0, _mediaPlayer.Time);
            var maximum = Math.Max(0, _mediaPlayer.Length - 1);
            var requested = Math.Clamp(initial + seconds * 1000L, 0, maximum);
            var reached = await SeekToDisplayedFrameAsync(requested);
            if (seconds < 0 && reached > initial)
                throw new InvalidOperationException("O retrocesso terminou depois da posição inicial.");
            _logger.Info($"Seek {seconds:+#;-#;0}s: inicial={initial} ms, solicitado={requested} ms, alcançado={reached} ms.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha no seek relativo de {seconds} segundo(s).", ex);
            ShowError("Não foi possível posicionar o vídeo no instante solicitado.", ex);
        }
        finally
        {
            if (ReferenceEquals(_currentMedia, media) && wasPlaying && restorePlayback) _mediaPlayer.Play();
            SetPlayerOperationState(false);
            _playerLifecycleGate.Release();
        }
    }

    private void ProgressSlider_DragStarted(object sender, MouseButtonEventArgs e) => _seeking = true;

    private async void ProgressSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_currentMedia is not null) await SeekAbsoluteAsync((long)ProgressSlider.Value);
        }
        finally { _seeking = false; }
    }

    private async void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSpeedSelection) return;
        if (SpeedCombo.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) return;
        var previousRate = _selectedPlaybackRate;
        if (_currentMedia is null)
        {
            _selectedPlaybackRate = rate;
            return;
        }

        try
        {
            await _playerLifecycleGate.WaitAsync();
            try
            {
                if (_playerDisposed || _currentMedia is null)
                {
                    _selectedPlaybackRate = rate;
                    return;
                }
                if (TryApplyPlaybackRate(rate))
                {
                    _selectedPlaybackRate = rate;
                    return;
                }
            }
            finally { _playerLifecycleGate.Release(); }

            SelectSpeed(previousRate);
            MessageBox.Show($"O formato deste vídeo não aceita {rate:0.##}x. A velocidade anterior foi mantida.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SelectSpeed(previousRate);
            LogPlayerControlFailure("alteração de velocidade", ex);
            MessageBox.Show("Não foi possível alterar a velocidade deste vídeo.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryApplyPlaybackRate(float rate) =>
        _mediaPlayer.SetRate(rate) == 0 && Math.Abs(_mediaPlayer.Rate - rate) <= 0.05f;

    private void SelectSpeed(float rate)
    {
        _updatingSpeedSelection = true;
        try
        {
            SpeedCombo.SelectedItem = SpeedCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Tag is string value && float.TryParse(value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var itemRate) && Math.Abs(itemRate - rate) <= 0.01f);
        }
        finally { _updatingSpeedSelection = false; }
    }

    private async Task SeekAbsoluteAsync(long requestedMilliseconds)
    {
        if (_currentMedia is null || _playerOperationInProgress) return;
        await _playerLifecycleGate.WaitAsync();
        if (_currentMedia is null)
        {
            _playerLifecycleGate.Release();
            return;
        }
        var media = _currentMedia;
        var resumePlayback = _mediaPlayer.IsPlaying;
        SetPlayerOperationState(true);
        try
        {
            await WaitForMediaReadyAsync();
            await PauseOnDisplayedFrameAsync();
            var initial = Math.Max(0, _mediaPlayer.Time);
            var reached = await SeekToDisplayedFrameAsync(requestedMilliseconds);
            _logger.Info($"Seek absoluto: inicial={initial} ms, solicitado={requestedMilliseconds} ms, alcançado={reached} ms.");
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao sincronizar a posição do vídeo.", ex);
            ShowError("Não foi possível posicionar o vídeo no instante solicitado.", ex);
        }
        finally
        {
            if (ReferenceEquals(_currentMedia, media) && resumePlayback) _mediaPlayer.Play();
            SetPlayerOperationState(false);
            _playerLifecycleGate.Release();
        }
    }

    private async Task<long> SeekToDisplayedFrameAsync(long requestedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (!_mediaPlayer.IsSeekable)
            throw new InvalidOperationException("Este formato de vídeo não permite navegação precisa.");
        var maximum = Math.Max(0, _mediaPlayer.Length - 1);
        var target = Math.Clamp(requestedMilliseconds, 0, maximum);
        try
        {
            var restartDecoder = _mediaPlayer.State is VLCState.Ended or VLCState.Stopped;
            if (restartDecoder)
            {
                _mediaPlayer.Stop();
                await Task.Delay(80, cancellationToken);
            }
            else
            {
                _mediaPlayer.SetPause(true);
                await Task.Delay(45, cancellationToken);
            }
            var started = restartDecoder && _currentMedia is not null
                ? _mediaPlayer.Play(_currentMedia)
                : _mediaPlayer.Play();
            if (!started)
                throw new InvalidOperationException("O LibVLC não conseguiu ativar o decodificador para navegar no vídeo.");
            for (var attempt = 0; attempt < 200 && !_mediaPlayer.IsPlaying; attempt++)
                await Task.Delay(25, cancellationToken);
            if (!_mediaPlayer.IsPlaying)
                throw new InvalidOperationException("O decodificador do vídeo não iniciou para realizar o seek.");
            _mediaPlayer.SetRate(1f);
            _mediaPlayer.Time = target;
            var reachedTarget = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (Math.Abs(_mediaPlayer.Time - target) <= 120) { reachedTarget = true; break; }
                await Task.Delay(25, cancellationToken);
            }
            if (!reachedTarget)
                throw new InvalidOperationException("O player não alcançou o timestamp solicitado.");
            await Task.Delay(35, cancellationToken);
            _mediaPlayer.SetPause(true);
            await PauseOnDisplayedFrameAsync();
            var decoded = Math.Max(0, _mediaPlayer.Time);
            if (Math.Abs(decoded - target) > 180)
                throw new InvalidOperationException("O LibVLC não apresentou o quadro correspondente ao timestamp solicitado.");

            _mediaPlayer.Time = target;
            await Task.Delay(30, cancellationToken);
            var reached = Math.Max(0, _mediaPlayer.Time);
            if (Math.Abs(reached - target) > 80)
                throw new InvalidOperationException("O timestamp final não corresponde ao destino solicitado.");
            RefreshPlayerPositionUnsafe();
            return reached;
        }
        finally { _mediaPlayer.SetRate(_selectedPlaybackRate); }
    }

    private void SetPlayerOperationState(bool inProgress)
    {
        _playerOperationInProgress = inProgress;
        _seeking = inProgress;
        UpdatePlayerControlState();
    }

    private void UpdatePlayerControlState()
    {
        var enabled = _currentMedia is not null && !_playerOperationInProgress && !_captureInProgress &&
                      !_videoLoadInProgress;
        PlaybackControlsPanel.IsEnabled = enabled;
        ProgressSlider.IsEnabled = enabled;
    }

    private async void CaptureFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_captureInProgress || _currentProject is null || _currentSection is null || _currentVideo is null || IsReadOnly) return;
        _captureInProgress = true;
        CaptureFrameOverlayText.Text = "Capturando...";
        UpdateInspectionWriteState();
        var lockTaken = false;
        try
        {
            await _playerLifecycleGate.WaitAsync();
            lockTaken = true;
            if (_currentMedia is null) throw new InvalidOperationException("Nenhum vídeo está carregado para captura.");
            SetPlayerOperationState(true);
            await PauseOnDisplayedFrameAsync();
            await WaitForDisplayedFrameAsync(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)));
            _captureDraft = await _frameCaptureService.CaptureCurrentFrameAsync(
                _currentProject, _currentSection, _currentVideo, _mediaPlayer, IsReadOnly);
            await AcceptCaptureDraftAsync(true);
        }
        catch (Exception ex) { ShowError("Não foi possível capturar o frame diretamente do vídeo.", ex); }
        finally
        {
            if (lockTaken) _playerLifecycleGate.Release();
            _captureInProgress = false;
            SetPlayerOperationState(false);
            CaptureFrameOverlayText.Text = "Capturar evidência";
            UpdateInspectionWriteState();
        }
    }

    private void RetakeCapture_Click(object sender, RoutedEventArgs e)
        => DiscardCaptureDraft();

    private void DiscardCaptureDraft()
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
        if (_currentSection is null || _captureDraft.SectionId != _currentSection.Id)
        {
            DiscardCaptureDraft();
            ShowError("A captura foi cancelada porque o trecho selecionado mudou.",
                new InvalidOperationException("Contexto da captura alterado."));
            return;
        }
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
        if (_currentProject is null || _currentSection is null || IsReadOnly || IsSectionReviewLocked) return;
        Capture? automaticCapture = null;
        var captureGateTaken = false;
        try
        {
            if (_currentVideo is not null && _currentMedia is not null &&
                !_videoLoadInProgress && !_playerOperationInProgress)
            {
                captureGateTaken = _playerLifecycleGate.Wait(0);
                if (captureGateTaken && _currentMedia is not null)
                {
                    using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await PauseOnDisplayedFrameAsync();
                    var draft = await _frameCaptureService.CaptureCurrentFrameAsync(
                        _currentProject, _currentSection, _currentVideo, _mediaPlayer, IsReadOnly,
                        captureTimeout.Token);
                    automaticCapture = _frameCaptureService.Accept(_currentProject, draft, IsReadOnly);
                }
                else if (!captureGateTaken)
                {
                    _logger.Info("Captura automática ignorada porque o player estava ocupado; " +
                                 "o cadastro da anomalia será aberto sem imagem inicial.");
                }
            }
        }
        catch (Exception ex)
        {
            if (automaticCapture is not null && automaticCapture.Status == CaptureStatus.Pending)
                _frameCaptureService.DeletePending(_currentProject, automaticCapture, IsReadOnly);
            automaticCapture = null;
            _logger.Error("Falha na captura automática; o cadastro da anomalia continuará sem imagem inicial.", ex);
        }
        finally
        {
            if (captureGateTaken) _playerLifecycleGate.Release();
        }

        await OpenOccurrenceEditorAsync(null, automaticCapture, automaticCapture);
    }

    private async void OpenPendingCaptures_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null) return;
        var sectionId = _currentSection.Id;
        var items = _currentProject.Captures
            .Where(x => x.SectionId == sectionId && x.Status == CaptureStatus.Pending)
            .OrderBy(x => x.Order)
            .Select(x => new PendingCaptureListItem(
                x.Id,
                x.FilePath,
                FormatTime(x.VideoTimestamp),
                FindVideo(x.VideoReferenceId)?.DisplayName ?? Path.GetFileName(x.VideoFilePath),
                !IsReadOnly && !IsSectionReviewLocked,
                !IsReadOnly))
            .ToList();
        if (items.Count == 0)
        {
            RefreshCaptures();
            return;
        }

        PendingCaptureAction action;
        Guid? captureId;
        HideVideoSurfaceForOverlay();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var window = new PendingCapturesWindow(items, LoadThumbnailOnDemandAsync) { Owner = this };
            window.ShowDialog();
            action = window.SelectedAction;
            captureId = window.SelectedCaptureId;
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao abrir a janela de capturas pendentes.", ex);
            ShowError("Não foi possível abrir as capturas pendentes.", ex);
            return;
        }
        finally
        {
            RestoreVideoSurfaceAfterOverlay();
        }

        if (captureId is not Guid id || _currentProject is null) return;
        switch (action)
        {
            case PendingCaptureAction.Register:
                if (!IsReadOnly && !IsSectionReviewLocked)
                    await OpenOccurrenceEditorAsync(null, _currentProject.Captures.SingleOrDefault(x => x.Id == id));
                break;
            case PendingCaptureAction.Navigate:
                await NavigateToCaptureAsync(id);
                break;
            case PendingCaptureAction.Delete:
                await DeletePendingCaptureAsync(id);
                break;
        }
    }

    private async void EditOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection is null || sender is not FrameworkElement { Tag: Guid id }) return;
        await OpenOccurrenceEditorAsync(_currentSection.Occurrences.SingleOrDefault(x => x.Id == id), null);
    }

    private async void DuplicateOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSection is null || IsReadOnly || IsSectionReviewLocked || sender is not FrameworkElement { Tag: Guid id }) return;
        await OpenOccurrenceEditorAsync(_currentSection.Occurrences.SingleOrDefault(x => x.Id == id), null, true);
    }

    private async Task OpenOccurrenceEditorAsync(Occurrence? occurrence, Capture? initialCapture, bool duplicate = false) =>
        await OpenOccurrenceEditorAsync(occurrence, initialCapture, null, duplicate);

    private async Task OpenOccurrenceEditorAsync(Occurrence? occurrence, Capture? initialCapture,
        Capture? automaticCapture, bool duplicate = false)
    {
        var editorReadOnly = IsReadOnly || IsSectionReviewLocked;
        if (_currentProject is null || _currentSection is null || (occurrence is null && editorReadOnly)) return;
        try
        {
            var catalog = await _store.ListAnomalyDefinitionsAsync();
            var editor = new OccurrenceWindow(_currentProject, _currentSection, catalog, _occurrenceService,
                occurrence, initialCapture, editorReadOnly, duplicate) { IsEmbedded = true };
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
            if (editor.ResumeVideoAfterSave && _currentMedia is not null) await ResumePlaybackAsync();
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
        await _playerLifecycleGate.WaitAsync();
        try
        {
            if (_currentMedia is null) return null;
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
        finally { _playerLifecycleGate.Release(); }
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
        var generation = Interlocked.Increment(ref _videoSurfaceGeneration);
        VideoOverlayLayer.Visibility = Visibility.Collapsed;
        PlayerControlsLayer.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Collapsed;
        VideoView.UpdateLayout();
        _ = PauseAndDetachVideoSurfaceAsync(generation);
    }

    private async Task PauseAndDetachVideoSurfaceAsync(long generation)
    {
        try
        {
            await _playerLifecycleGate.WaitAsync();
            try
            {
                if (_playerDisposed || generation != Volatile.Read(ref _videoSurfaceGeneration)) return;
                if (_currentMedia is not null) _mediaPlayer.SetPause(true);
                VideoView.MediaPlayer = null;
                VideoView.UpdateLayout();
            }
            finally { _playerLifecycleGate.Release(); }
        }
        catch (Exception ex) { LogPlayerControlFailure("ocultação da superfície de vídeo", ex); }
    }

    private void RestoreVideoSurfaceAfterOverlay()
    {
        var generation = Interlocked.Increment(ref _videoSurfaceGeneration);
        VideoView.Visibility = Visibility.Visible;
        VideoOverlayLayer.Visibility = Visibility.Visible;
        PlayerControlsLayer.Visibility = Visibility.Visible;
        VideoView.UpdateLayout();
        _ = AttachVideoSurfaceAsync(generation);
    }

    private async Task AttachVideoSurfaceAsync(long generation)
    {
        try
        {
            await _playerLifecycleGate.WaitAsync();
            try
            {
                if (_playerDisposed || generation != Volatile.Read(ref _videoSurfaceGeneration)) return;
                VideoView.MediaPlayer = _mediaPlayer;
                VideoView.UpdateLayout();
            }
            finally { _playerLifecycleGate.Release(); }
        }
        catch (Exception ex) { LogPlayerControlFailure("restauração da superfície de vídeo", ex); }
    }

    private async Task ResumePlaybackAsync()
    {
        await _playerLifecycleGate.WaitAsync();
        try
        {
            if (!_playerDisposed && _currentMedia is not null && !_mediaPlayer.Play())
                throw new InvalidOperationException("O LibVLC não conseguiu retomar a reprodução.");
        }
        finally { _playerLifecycleGate.Release(); }
    }

    private async Task<long> WaitForDisplayedFrameAsync(TimeSpan target)
    {
        if (!_mediaPlayer.IsSeekable)
            throw new InvalidOperationException("Este formato de vídeo não permite confirmar o quadro solicitado.");
        _mediaPlayer.SetPause(true);
        var targetMilliseconds = Math.Max(0, (long)target.TotalMilliseconds);
        var reachedTarget = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (Math.Abs(_mediaPlayer.Time - targetMilliseconds) <= 120) { reachedTarget = true; break; }
            await Task.Delay(25);
        }
        if (!reachedTarget)
            throw new InvalidOperationException("O player não alcançou o timestamp solicitado.");
        await Task.Delay(160);
        var reached = Math.Max(0, _mediaPlayer.Time);
        if (Math.Abs(reached - targetMilliseconds) > 120)
            throw new InvalidOperationException("O quadro exibido não permaneceu estável no timestamp solicitado.");
        return reached;
    }

    private async void DeleteOccurrence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || _currentSection is null || sender is not FrameworkElement { Tag: Guid id } ||
            IsReadOnly || IsSectionReviewLocked) return;
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
        if (_currentProject is null || _currentSection is null || sender is not FrameworkElement { Tag: Guid id }) return;
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
        await LoadVideoAsync(video, MediaLoadIntent.NavigateToEvidence, timestamp);
    }

    private async Task DeletePendingCaptureAsync(Guid id)
    {
        if (_currentProject is null || IsReadOnly) return;
        var capture = _currentProject.Captures.SingleOrDefault(x => x.Id == id);
        if (capture is null)
        {
            MessageBox.Show("A captura pendente não foi encontrada.", "GeoAutoDocs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshCaptures();
            return;
        }
        if (MessageBox.Show("Excluir esta captura pendente e seu arquivo PNG?", "Confirmar exclusão",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _frameCaptureService.DeletePending(_currentProject, capture, IsReadOnly);
        RefreshCaptures();
        await SaveInspectionChangesAsync();
    }

    private async Task NavigateToCaptureAsync(Guid id)
    {
        if (_currentProject is null) return;
        var capture = _currentProject.Captures.SingleOrDefault(x => x.Id == id);
        if (capture is null)
        {
            MessageBox.Show("A captura pendente não foi encontrada.", "GeoAutoDocs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshCaptures();
            return;
        }
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
        await LoadVideoAsync(video, MediaLoadIntent.NavigateToEvidence, capture.VideoTimestamp);
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
        await LoadVideoAsync(_currentVideo, MediaLoadIntent.RelocatedVideo);
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
        await StopAndReleaseCurrentMediaAsync();
        _currentSection.VideoReferences.Remove(_currentVideo);
        _currentVideo = null;
        RefreshVideos();
        await SaveInspectionChangesAsync();
    }

    private async Task SaveSectionMetadataAsync(InspectionSection section)
    {
        if (_currentProject is null || IsReadOnly) return;
        await _persistenceGate.WaitAsync();
        try
        {
            SaveStatus.Text = "Salvando...";
            await _store.SaveSectionAsync(section);
            _currentProject.UpdatedAt = DateTime.UtcNow;
            if (_sharedSession is not null) _sharedDirty = true;
            _saveErrorNotified = false;
            SaveStatus.Text = "✓ Salvo";
            RefreshHomeStatus();
            UpdateSectionReviewState();
        }
        catch (Exception ex)
        {
            MarkPendingChanges();
            _saveTimer.Stop();
            _saveTimer.Start();
            SaveStatus.Text = "Falha ao salvar — nova tentativa pendente";
            UpdateSectionReviewState();
            _logger.Error("Não foi possível salvar os metadados do trecho.", ex);
            if (!_saveErrorNotified)
            {
                _saveErrorNotified = true;
                RefreshHomeStatus();
                MessageBox.Show("Não foi possível salvar o trecho. Uma nova tentativa será feita automaticamente.",
                    "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task SaveInspectionChangesAsync()
    {
        if (_currentProject is null || IsReadOnly) return;
        MarkPendingChanges();
        UpdateSectionReviewState();
        await SavePendingAsync();
        if (_sharedSession is not null) _sharedDirty = true;
    }

    private void RefreshPlayerPosition()
    {
        if (_playerDisposed || !_playerLifecycleGate.Wait(0)) return;
        try { RefreshPlayerPositionUnsafe(); }
        catch (Exception ex)
        {
            if (_playerTelemetryErrorNotified) return;
            _playerTelemetryErrorNotified = true;
            LogPlayerControlFailure("leitura da posição", ex);
        }
        finally { _playerLifecycleGate.Release(); }
    }

    private void RefreshPlayerPositionUnsafe()
    {
        var duration = Math.Max(0, _mediaPlayer.Length);
        var current = _mediaPlayer.State == VLCState.Ended && duration > 0
            ? duration : Math.Max(0, _mediaPlayer.Time);
        if (current > 0) _lastKnownPlayerTime = current;
        if (!_seeking)
        {
            ProgressSlider.Maximum = Math.Max(1, duration);
            ProgressSlider.Value = Math.Min(current, ProgressSlider.Maximum);
        }
        TimeText.Text = $"{FormatTime(TimeSpan.FromMilliseconds(current))} / {FormatTime(TimeSpan.FromMilliseconds(duration))}";
    }

    private void HandlePlayerError()
    {
        if (_playerErrorNotified || _playerDisposed || _currentMedia is null) return;
        _playerErrorNotified = true;
        ShowVideoState("Verifique o arquivo, codec ou acesso ao local de armazenamento.", false,
            VideoSurfaceState.Error);
        _logger.Error(
            $"Erro de codec, rede ou leitura. Caminho='{_currentVideo?.FilePath}', " +
            $"extensão='{Path.GetExtension(_currentVideo?.FilePath)}', backend='LibVLC', " +
            $"geracao={Volatile.Read(ref _activeMediaGeneration)}.",
            new IOException("O LibVLC informou falha durante a reprodução."));
    }

    private async Task StopAndReleaseCurrentMediaAsync(CancellationToken cancellationToken = default)
    {
        if (_playerDisposed) return;
        Interlocked.Increment(ref _mediaGeneration);
        _mediaLoadCts?.Cancel();
        await _playerLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Exchange(ref _activeMediaGeneration, 0);
            try { _mediaPlayer.Stop(); }
            catch (Exception ex) { _logger.Error("Falha ao interromper o player.", ex); }
            var media = _currentMedia;
            _currentMedia = null;
            try { media?.Dispose(); }
            catch (Exception ex) { _logger.Error("Falha ao descartar a mídia atual.", ex); }
            _lastKnownPlayerTime = 0;
            _videoLoadInProgress = false;
            _playerTelemetryErrorNotified = false;
            if (_currentSection is null)
                ShowVideoState("Escolha um local e um trecho para continuar.", false);
            else if (_currentSection.VideoReferences.Count == 0)
                ShowVideoState("Este trecho ainda não possui vídeos vinculados.", !IsReadOnly);
            else
                ShowVideoState("Escolha um dos vídeos vinculados ao trecho.", false);
            PlayPauseButton.Content = "▶";
        }
        finally { _playerLifecycleGate.Release(); }
    }

    private void ShowVideoState(string message, bool showAddVideo,
        VideoSurfaceState state = VideoSurfaceState.Empty)
    {
        VideoLoadedBadge.Visibility = Visibility.Collapsed;
        PlaybackControlsPanel.IsEnabled = false;
        ProgressSlider.IsEnabled = false;
        VideoStateTitle.Text = state switch
        {
            VideoSurfaceState.Loading => "Carregando vídeo",
            VideoSurfaceState.Missing => "Arquivo de vídeo não encontrado",
            VideoSurfaceState.Error => "Não foi possível reproduzir o vídeo",
            _ when _currentSection is null => "Selecione o contexto da inspeção",
            _ when showAddVideo => "Adicione o primeiro vídeo",
            _ when _currentVideo is not null => "Vídeo pronto para carregar",
            _ => "Selecione um vídeo"
        };
        VideoStateIcon.Text = state switch
        {
            VideoSurfaceState.Loading => "\uE895",
            VideoSurfaceState.Missing => "\uE783",
            VideoSurfaceState.Error => "\uEA39",
            _ => "\uE714"
        };
        VideoStateIcon.Foreground = (System.Windows.Media.Brush)FindResource(
            state is VideoSurfaceState.Missing or VideoSurfaceState.Error ? "DangerBrush" :
            state == VideoSurfaceState.Loading ? "PrimaryBrush" : "TextMutedBrush");
        VideoMessage.Text = message;
        EmptyVideoButton.Visibility = showAddVideo ? Visibility.Visible : Visibility.Collapsed;
        EmptyRelocateVideoButton.Visibility = state == VideoSurfaceState.Missing && !IsReadOnly
            ? Visibility.Visible : Visibility.Collapsed;
        var canReload = _currentVideo is not null && state is not VideoSurfaceState.Loading and not VideoSurfaceState.Missing;
        RetryVideoButton.Content = state == VideoSurfaceState.Error ? "Tentar novamente" : "Recarregar vídeo";
        RetryVideoButton.Visibility = state == VideoSurfaceState.Error || canReload
            ? Visibility.Visible : Visibility.Collapsed;
        VideoEmptyState.Visibility = Visibility.Visible;
    }

    private async Task DisposePlayerAsync()
    {
        if (_playerDisposed || _playerDisposeInProgress) return;
        _playerDisposeInProgress = true;
        _inspectionLoadCts?.Cancel();
        _mediaLoadCts?.Cancel();
        _playerTimer.Stop();
        _overviewRefreshTimer.Stop();
        try
        {
            await _playerLifecycleGate.WaitAsync();
            try
            {
                _playerDisposed = true;
                Interlocked.Increment(ref _mediaGeneration);
                Interlocked.Exchange(ref _activeMediaGeneration, 0);
                VideoView.MediaPlayer = null;
                try { _mediaPlayer.Stop(); }
                catch (Exception ex) { _logger.Error("Falha ao interromper o player durante o fechamento.", ex); }
                try { _currentMedia?.Dispose(); }
                catch (Exception ex) { _logger.Error("Falha ao descartar a mídia durante o fechamento.", ex); }
                _currentMedia = null;
                _libVlc.Log -= OnLibVlcLog;
                try { _mediaPlayer.Dispose(); }
                catch (Exception ex) { _logger.Error("Falha ao descartar o MediaPlayer.", ex); }
                try { _libVlc.Dispose(); }
                catch (Exception ex) { _logger.Error("Falha ao descartar o LibVLC.", ex); }
            }
            finally { _playerLifecycleGate.Release(); }

            _inspectionLoadCts?.Dispose();
            _inspectionLoadCts = null;
            _mediaLoadCts?.Dispose();
            _mediaLoadCts = null;
        }
        finally { _playerDisposeInProgress = false; }
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

        if (e.Key == Key.Escape && _videoFocusMode)
        {
            ToggleVideoFocus();
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
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 220;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Thumbnail ignorada para '{path}': {ex.Message}");
            return null;
        }
    }

    private async Task<BitmapImage?> LoadThumbnailOnDemandAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_thumbnailFailures.TryGetValue(path, out var failedAt) &&
            DateTimeOffset.UtcNow - failedAt < TimeSpan.FromSeconds(30)) return null;
        _thumbnailFailures.TryRemove(path, out _);
        lock (_thumbnailCacheSync)
            if (_thumbnailCache.TryGetValue(path, out var cached)) return cached;

        try
        {
            return await _thumbnailLoadExecutor.RunAsync(async token =>
            {
                lock (_thumbnailCacheSync)
                    if (_thumbnailCache.TryGetValue(path, out var cached)) return cached;
                var image = await Task.Run(() => DecodeThumbnail(path), token);
                token.ThrowIfCancellationRequested();
                RememberThumbnail(path, image);
                _thumbnailFailures.TryRemove(path, out _);
                return image;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_thumbnailFailures.Count > 256) _thumbnailFailures.Clear();
            if (_thumbnailFailures.TryAdd(path, DateTimeOffset.UtcNow))
                _logger.Error($"Falha de miniatura. Caminho='{path}'. O item continuará sem imagem.", ex);
            return null;
        }
    }

    private static BitmapImage DecodeThumbnail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 220;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RememberThumbnail(string path, BitmapImage image)
    {
        const int maximumCachedThumbnails = 64;
        lock (_thumbnailCacheSync)
        {
            if (_thumbnailCache.ContainsKey(path)) return;
            _thumbnailCache[path] = image;
            _thumbnailCacheOrder.AddLast(path);
            while (_thumbnailCacheOrder.Count > maximumCachedThumbnails)
            {
                var oldest = _thumbnailCacheOrder.First!.Value;
                _thumbnailCacheOrder.RemoveFirst();
                _thumbnailCache.Remove(oldest);
            }
        }
    }

    private sealed class VideoListItem : INotifyPropertyChanged
    {
        private readonly VideoReference _video;
        private bool? _isAvailable;
        public VideoListItem(VideoReference video) => _video = video;
        public VideoReference Video => _video;
        public Guid Id => _video.Id;
        public string DisplayName => string.IsNullOrWhiteSpace(_video.DisplayName) ? Path.GetFileName(_video.FilePath) : _video.DisplayName;
        public string Availability => _isAvailable switch
        {
            true => "Disponível",
            false => "Vídeo não encontrado",
            null => "Verificando disponibilidade..."
        };
        public string StatusColor => _isAvailable switch { true => "#39D17E", false => "#EC6670", null => "#8295A7" };
        public bool? IsAvailable => _isAvailable;
        public string PathKind => _video.Status == MediaReferenceStatus.SharedUnc ? "Arquivo em rede (UNC)" : "Arquivo local";
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetAvailability(bool available)
        {
            if (_isAvailable == available) return;
            _isAvailable = available;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Availability)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
        }
    }

    private sealed class DevelopmentImagePreviewItem(string filePath)
    {
        public BitmapImage? Thumbnail { get; } = LoadThumbnail(filePath);
        public string DisplayName { get; } = Path.GetFileName(filePath);
    }

    private sealed class OccurrenceListItem
    {
        public Guid Id { get; }
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
        public string ModifyToolTip { get; }
        public string EditToolTip => CanModify ? "Editar" : ModifyToolTip;
        public bool IsComplete { get; }

        public OccurrenceListItem(Occurrence occurrence, bool canModify, string modifyToolTip)
        {
            Id = occurrence.Id;
            CanModify = canModify;
            ModifyToolTip = modifyToolTip;
            Location = OccurrenceRules.FormatLocation(occurrence);
            Anomalies = string.Join(" + ", occurrence.Anomalies.OrderBy(x => x.Order).Select(x =>
                x.AnomalyType.AllowsCustomText && !string.IsNullOrWhiteSpace(x.CustomText)
                    ? $"Outro: {x.CustomText}" : x.DisplayName)) + (occurrence.IsSevere ? " (grave)" : "");
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
                return values.Count == 0 ? "" : $"{x.DisplayName}: {string.Join(", ", values)}";
            }).Where(x => x.Length > 0));
            CanGoToVideo = occurrence.Evidence.Any(x => x.VideoReferenceId is not null && x.VideoTimestamp is not null);
            IsComplete = OccurrenceRules.IsComplete(occurrence);
            var pending = OccurrenceRules.GetPendingReasons(occurrence);
            Completeness = IsComplete ? "✓ Completa" : "⚠ " + string.Join(" • ", pending);
            CompletenessColor = IsComplete ? "#39D17E" : "#F2B84B";
        }
    }

    private sealed class ReportSectionSelectionItem(
        Guid sectionId,
        string displayName,
        bool isSelected) : INotifyPropertyChanged
    {
        private bool _isSelected = isSelected;

        public Guid SectionId { get; } = sectionId;
        public string DisplayName { get; } = displayName;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record OverviewReportItem(Guid Id, string Title, string Detail, string Status, string StatusColor);

    private sealed record InspectionLayoutPreferences(double SideWidth, bool SideVisible, bool ContextExpanded,
        bool FocusMode = false);

    private sealed class OverviewActivityItem(string icon, string title, string detail, DateTime timestamp)
    {
        public string Icon { get; } = icon;
        public string Title { get; } = title;
        public string Detail { get; } = detail;
        public DateTime Timestamp { get; } = timestamp;
        public string When { get; } = timestamp.ToString("dd/MM/yyyy HH:mm");
    }

    private void MarkPendingChanges()
    {
        _hasPendingChanges = true;
        _pendingSaveRevision++;
    }

    private void ScheduleSave()
    {
        if (_loadingFields || _currentProject is null || _sharedSession?.IsReadOnly == true) return;
        MarkPendingChanges(); SaveStatus.Text = "Salvando..."; UpdateSectionReviewState();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void ShowError(string message, Exception ex)
    {
        _logger.Error(message, ex); MessageBox.Show(message, "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static int NextOrder(IEnumerable<int> orders) => orders.DefaultIfEmpty(-1).Max() + 1;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record TrayPreferences(bool CloseToTray, bool NoticeShown);
}
