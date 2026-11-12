using GeoAutoDocs.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class SchemaVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsSchemaVersionTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NewDatabaseRecordsCurrentSchemaVersionOnce()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);

        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.Equal(ProjectStore.CurrentSchemaVersion, await store.GetSchemaVersionAsync());
        Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM \"GeoAutoDocsSchemaVersion\""));
    }

    [Fact]
    public async Task DatabaseWithoutVersionTableIsStampedWithoutLosingData()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        var project = await store.CreateAsync("Anterior ao controle de versão");
        await ExecuteAsync(paths, "DROP TABLE \"GeoAutoDocsSchemaVersion\"");
        Assert.Equal(0, await store.GetSchemaVersionAsync());

        await new ProjectStore(paths).InitializeAsync();

        Assert.Equal(ProjectStore.CurrentSchemaVersion, await store.GetSchemaVersionAsync());
        Assert.Equal("Anterior ao controle de versão", (await store.GetAsync(project.Id))!.Name);
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutChangingTheDatabase()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        var newer = ProjectStore.CurrentSchemaVersion + 1;
        await ExecuteAsync(paths,
            $"INSERT INTO \"GeoAutoDocsSchemaVersion\" (\"Version\", \"AppliedAtUtc\") VALUES ({newer}, '2030-01-01T00:00:00Z')");

        var error = await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => new ProjectStore(paths).InitializeAsync());

        Assert.Equal(newer, error.DatabaseVersion);
        Assert.Equal(ProjectStore.CurrentSchemaVersion, error.SupportedVersion);
        Assert.Equal(newer, await store.GetSchemaVersionAsync());
        Assert.Equal(2L, await ScalarAsync(paths, "SELECT COUNT(*) FROM \"GeoAutoDocsSchemaVersion\""));
    }

    private static async Task ExecuteAsync(AppDataPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(AppDataPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
