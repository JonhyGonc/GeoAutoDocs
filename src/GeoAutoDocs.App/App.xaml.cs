using System.Windows;
using System.Windows.Threading;
using GeoAutoDocs.Infrastructure;

namespace GeoAutoDocs.App;

public partial class App : Application
{
    private LocalLogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new AppDataPaths();
        paths.EnsureDirectories();
        _logger = new LocalLogger(paths.LogPath);
        _logger.Info("Aplicação iniciada.");

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            var store = new ProjectStore(paths);
            await store.InitializeAsync();
            new MainWindow(store, new WordReportService(paths), _logger).Show();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao inicializar banco de dados.", ex);
            MessageBox.Show("Não foi possível iniciar o aplicativo. Consulte o arquivo de log.",
                "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Erro não tratado.", e.Exception);
        MessageBox.Show("Ocorreu um erro inesperado. Os detalhes foram registrados no log.",
            "GeoAutoDocs", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
