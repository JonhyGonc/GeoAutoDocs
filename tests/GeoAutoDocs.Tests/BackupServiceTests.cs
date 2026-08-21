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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
