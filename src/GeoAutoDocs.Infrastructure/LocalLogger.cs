namespace GeoAutoDocs.Infrastructure;

public sealed class LocalLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private readonly string _path;
    private readonly object _sync = new();

    public LocalLogger(string path) => _path = path;

    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded();
                File.AppendAllText(_path,
                    $"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? "" : Environment.NewLine + exception)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never terminate the desktop application.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaxLogBytes) return;
        File.Move(_path, _path + ".1", true);
    }
}
