namespace GeoAutoDocs.Infrastructure;

public sealed class LocalLogger
{
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
                File.AppendAllText(_path,
                    $"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? "" : Environment.NewLine + exception)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never terminate the desktop application.
        }
    }
}

