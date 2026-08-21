using System.IO;
using System.Windows;
using System.Windows.Threading;
using GeoAutoDocs.Infrastructure;

namespace GeoAutoDocs.App;

public partial class App : Application
{
    private LocalLogger? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "Local\\GeoAutoDocs.SingleInstance", out var isFirstInstance);
        _ownsSingleInstance = isFirstInstance;
        if (!isFirstInstance)
        {
            MessageBox.Show("O GeoAutoDocs já está aberto ou iniciando.", "GeoAutoDocs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var paths = new AppDataPaths(Environment.GetEnvironmentVariable("GEOAUTODOCS_DATA_ROOT"));
        paths.EnsureDirectories();
        _logger = new LocalLogger(paths.LogPath);
        _logger.Info("Aplicação iniciada.");

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            var store = new ProjectStore(paths);
            var backups = new BackupService(paths, _logger);
            var availableBackup = backups.GetStatus();
            if (!File.Exists(paths.DatabasePath) && availableBackup.HasSuccessfulBackup &&
                MessageBox.Show("O banco de dados não foi encontrado. Restaurar o último backup?", "GeoAutoDocs",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await backups.RestoreAsync(availableBackup.LastBackupPath!, overwriteExisting: false, _shutdown.Token);
            try { await store.InitializeAsync(); }
            catch when (availableBackup.HasSuccessfulBackup && MessageBox.Show(
                            "O banco de dados não pôde ser aberto. Restaurar o último backup válido?\n\nO banco atual será preservado antes da restauração.",
                            "Recuperar dados", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes)
            {
                await backups.RestoreAsync(availableBackup.LastBackupPath!, overwriteExisting: true, _shutdown.Token);
                await store.InitializeAsync();
            }
            var exchangeSerializer = new InspectionExchangeSerializer();
            var exchangeService = new InspectionExchangeService(exchangeSerializer);
            var geoAutoEngineTransport = new NamedPipeInspectionTransport(exchangeSerializer);
            var window = new MainWindow(store, new WordReportService(paths, exchangeService), new SharedProjectService(paths), _logger,
                new FrameCaptureService(paths, _logger), new OccurrenceService(paths, _logger),
                new AnomalyCatalogService(paths), exchangeService, geoAutoEngineTransport, availableBackup);
            window.Show();
            _ = RunStartupBackupAsync(backups, window);
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao inicializar banco de dados.", ex);
            MessageBox.Show("Não foi possível iniciar o aplicativo. Consulte o arquivo de log.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunStartupBackupAsync(BackupService backups, MainWindow window)
    {
        try
        {
            var status = await Task.Run(() => backups.CreateIfChangedAsync(_shutdown.Token), _shutdown.Token);
            if (!_shutdown.IsCancellationRequested) window.UpdateBackupStatus(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger?.Error("Falha no backup automático em segundo plano.", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Erro não tratado.", e.Exception);
        MessageBox.Show("Ocorreu um erro inesperado. Os detalhes foram registrados no log.",
            "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
