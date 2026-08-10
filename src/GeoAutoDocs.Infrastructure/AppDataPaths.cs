namespace GeoAutoDocs.Infrastructure;

public sealed class AppDataPaths
{
    public string Root { get; }
    public string DatabaseDirectory => Path.Combine(Root, "database");
    public string ProjectsDirectory => Path.Combine(Root, "projects");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string CacheDirectory => Path.Combine(Root, "Cache");
    public string DatabasePath => Path.Combine(DatabaseDirectory, "GeoAutoDocs.db");
    public string LogPath => Path.Combine(LogsDirectory, "GeoAutoDocs.log");

    public AppDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeoAutoDocs");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
