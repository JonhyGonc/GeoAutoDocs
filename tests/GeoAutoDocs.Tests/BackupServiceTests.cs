using System.IO.Compression;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsBackupTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreatesRestorableBackupRetainsHistoryAndPreservesLastValidOnFailure()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        var project = await store.CreateAsync("Backup");
        await File.WriteAllTextAsync(Path.Combine(project.ProjectPath!, "evidence.txt"), "evidência");
        var service = new BackupService(paths, new LocalLogger(paths.LogPath), retentionCount: 2);

        BackupStatus status = new();
        for (var index = 0; index < 3; index++) status = await service.CreateAsync();

        Assert.True(status.HasSuccessfulBackup);
        Assert.Equal(2, Directory.GetFiles(paths.BackupsDirectory, "GeoAutoDocs_*.zip").Length);
        using (var archive = ZipFile.OpenRead(status.LastBackupPath!))
        {
            Assert.Contains(archive.Entries, x => x.FullName == "GeoAutoDocs.db");
            Assert.Contains(archive.Entries, x => x.FullName.EndsWith("evidence.txt"));
        }
        Assert.Equal(status.LastSuccessfulAt, service.GetStatus().LastSuccessfulAt);

        var unchanged = await service.CreateIfChangedAsync();
        Assert.Equal(status.LastBackupPath, unchanged.LastBackupPath);
        Assert.Equal(2, Directory.GetFiles(paths.BackupsDirectory, "GeoAutoDocs_*.zip").Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(status.LastBackupPath!, overwriteExisting: false));

        File.Delete(paths.DatabasePath);
        var failed = await service.CreateAsync();
        Assert.NotNull(failed.LastError);
        Assert.Equal(status.LastBackupPath, failed.LastBackupPath);
        Assert.True(File.Exists(status.LastBackupPath));
        await service.RestoreAsync(status.LastBackupPath!, overwriteExisting: false);
        var restored = await new ProjectStore(paths).GetAsync(project.Id);
        Assert.Equal("Backup", restored!.Name);
        Assert.True(File.Exists(Path.Combine(restored.ProjectPath!, "evidence.txt")));
    }

    [Fact]
    public async Task BackupIncludesCacheButSkipsTemporaryFilesAndRestoresIt()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        await store.CreateAsync("Compartilhado");
        var sharedCache = Path.Combine(paths.CacheDirectory, Guid.NewGuid().ToString("N"));
        var capturePath = Path.Combine(sharedCache, "captures", "captura.png");
        Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
        await File.WriteAllTextAsync(capturePath, "captura");
        await File.WriteAllTextAsync(Path.Combine(sharedCache, "project.geoadocs.json.tmp." + Guid.NewGuid().ToString("N")), "parcial");
        await File.WriteAllTextAsync(Path.Combine(paths.CacheDirectory, "report-continuation-x.tmp.docx"), "parcial");
        var service = new BackupService(paths, new LocalLogger(paths.LogPath));

        var status = await service.CreateAsync();

        Assert.True(status.HasSuccessfulBackup);
        using (var archive = ZipFile.OpenRead(status.LastBackupPath!))
        {
            var relative = "Cache/" + Path.GetRelativePath(paths.CacheDirectory, capturePath).Replace('\\', '/');
            Assert.Contains(archive.Entries, x => x.FullName.Replace('\\', '/') == relative);
            Assert.DoesNotContain(archive.Entries, x => x.FullName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase));
        }

        Directory.Delete(paths.CacheDirectory, true);
        File.Delete(paths.DatabasePath);
        await service.RestoreAsync(status.LastBackupPath!, overwriteExisting: false);

        Assert.Equal("captura", await File.ReadAllTextAsync(capturePath));
    }

    [Fact]
    public async Task ChangeOnlyInCacheTriggersNewBackup()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        await store.CreateAsync("Cache");
        var service = new BackupService(paths, new LocalLogger(paths.LogPath));
        var first = await service.CreateAsync();
        Assert.True(first.HasSuccessfulBackup);

        var evidence = Path.Combine(paths.CacheDirectory, Guid.NewGuid().ToString("N"), "evidence", "foto.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.WriteAllTextAsync(evidence, "foto");
        File.SetLastWriteTimeUtc(evidence, first.LastSuccessfulAt!.Value.UtcDateTime.AddMinutes(1));

        var second = await service.CreateIfChangedAsync();

        Assert.NotEqual(first.LastBackupPath, second.LastBackupPath);
    }

    [Fact]
    public async Task RestoresLegacyBackupWithoutCacheFolder()
    {
        var paths = new AppDataPaths(_root);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        var project = await store.CreateAsync("Legado");
        var service = new BackupService(paths, new LocalLogger(paths.LogPath));
        var status = await service.CreateAsync();

        var legacyArchive = Path.Combine(paths.BackupsDirectory, "legacy.zip");
        using (var source = ZipFile.OpenRead(status.LastBackupPath!))
        using (var target = ZipFile.Open(legacyArchive, ZipArchiveMode.Create))
            foreach (var entry in source.Entries.Where(x => !x.FullName.StartsWith("Cache", StringComparison.Ordinal)))
            {
                await using var input = entry.Open();
                await using var output = target.CreateEntry(entry.FullName).Open();
                await input.CopyToAsync(output);
            }

        File.Delete(paths.DatabasePath);
        await service.RestoreAsync(legacyArchive, overwriteExisting: false);

        Assert.Equal("Legado", (await new ProjectStore(paths).GetAsync(project.Id))!.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
