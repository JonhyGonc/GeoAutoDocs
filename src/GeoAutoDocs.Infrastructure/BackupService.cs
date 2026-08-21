using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GeoAutoDocs.Infrastructure;

public sealed record BackupStatus(
    DateTimeOffset? LastSuccessfulAt = null,
    string? LastBackupPath = null,
    DateTimeOffset? LastAttemptAt = null,
    string? LastError = null)
{
    public bool HasSuccessfulBackup => LastSuccessfulAt is not null &&
                                       !string.IsNullOrWhiteSpace(LastBackupPath) &&
                                       File.Exists(LastBackupPath);
}

public sealed class BackupService(AppDataPaths paths, LocalLogger logger, int retentionCount = 5)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public BackupStatus GetStatus()
    {
        try
        {
            if (!File.Exists(paths.BackupStatusPath)) return new BackupStatus();
            return JsonSerializer.Deserialize<BackupStatus>(File.ReadAllText(paths.BackupStatusPath), JsonOptions)
                   ?? new BackupStatus();
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao ler o status do backup.", ex);
            return new BackupStatus(LastError: "Status do backup indisponível.");
        }
    }

    public Task<BackupStatus> CreateIfChangedAsync(CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        if (status.HasSuccessfulBackup && !HasChangesSince(status.LastSuccessfulAt!.Value.UtcDateTime))
            return Task.FromResult(status with { LastError = null });
        return CreateAsync(cancellationToken);
    }

    private bool HasChangesSince(DateTime lastBackupUtc)
    {
        if (!File.Exists(paths.DatabasePath) || File.GetLastWriteTimeUtc(paths.DatabasePath) > lastBackupUtc) return true;
        return Directory.Exists(paths.ProjectsDirectory) && Directory.EnumerateFiles(
            paths.ProjectsDirectory, "*", SearchOption.AllDirectories).Any(x => File.GetLastWriteTimeUtc(x) > lastBackupUtc);
    }

    public async Task<BackupStatus> CreateAsync(CancellationToken cancellationToken = default)
    {
        var previous = GetStatus();
        var attemptedAt = DateTimeOffset.Now;
        var stagingDirectory = Path.Combine(paths.BackupsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        var temporaryArchive = Path.Combine(paths.BackupsDirectory, ".backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("Banco de dados não encontrado.", paths.DatabasePath);
            Directory.CreateDirectory(paths.BackupsDirectory);
            Directory.CreateDirectory(stagingDirectory);

            await CopyDatabaseAsync(Path.Combine(stagingDirectory, "GeoAutoDocs.db"), cancellationToken);
            await CopyManagedFilesAsync(stagingDirectory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ZipFile.CreateFromDirectory(stagingDirectory, temporaryArchive, CompressionLevel.Fastest, false);
            var archivePath = Path.Combine(paths.BackupsDirectory,
                $"GeoAutoDocs_{attemptedAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");
            File.Move(temporaryArchive, archivePath);

            var status = new BackupStatus(attemptedAt, archivePath, attemptedAt);
            await WriteStatusAsync(status, cancellationToken);
            ApplyRetention(archivePath);
            logger.Info($"Backup concluído: {archivePath}");
            return status;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var status = previous with { LastAttemptAt = attemptedAt, LastError = ex.Message };
            try { await WriteStatusAsync(status, CancellationToken.None); }
            catch (Exception statusError) { logger.Error("Falha ao registrar erro do backup.", statusError); }
            logger.Error("Falha ao criar backup.", ex);
            return status;
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task RestoreAsync(string archivePath, bool overwriteExisting,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Backup não encontrado.", archivePath);
        if (File.Exists(paths.DatabasePath) && !overwriteExisting)
            throw new InvalidOperationException("A restauração sobrescreveria os dados atuais e exige confirmação.");

        var stagingDirectory = Path.Combine(paths.BackupsDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            ExtractSafely(archivePath, stagingDirectory);
            var stagedDatabase = Path.Combine(stagingDirectory, "GeoAutoDocs.db");
            await ValidateDatabaseAsync(stagedDatabase, cancellationToken);

            if (File.Exists(paths.DatabasePath))
            {
                var preserved = Path.Combine(paths.BackupsDirectory,
                    $"GeoAutoDocs_pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.db");
                File.Copy(paths.DatabasePath, preserved, false);
            }

            Directory.CreateDirectory(paths.DatabaseDirectory);
            await CopyAtomicallyAsync(stagedDatabase, paths.DatabasePath, cancellationToken);
            var stagedProjects = Path.Combine(stagingDirectory, "projects");
            if (Directory.Exists(stagedProjects))
                foreach (var source in Directory.EnumerateFiles(stagedProjects, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(stagedProjects, source);
                    await CopyAtomicallyAsync(source, Path.Combine(paths.ProjectsDirectory, relative), cancellationToken);
                }
            logger.Info($"Backup restaurado: {archivePath}");
        }
        catch (Exception ex)
        {
            logger.Error($"Falha ao restaurar backup '{archivePath}'.", ex);
            throw;
        }
        finally { TryDeleteDirectory(stagingDirectory); }
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O backup contém um caminho inválido.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
    }

    private static async Task ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) throw new InvalidDataException("O backup não contém o banco de dados.");
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken)), "ok",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O banco contido no backup está corrompido.");
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Projects';";
        if (Convert.ToInt64(await schema.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidDataException("O backup não contém um banco GeoAutoDocs válido.");
    }

    private static async Task CopyAtomicallyAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".restore." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, true);
        }
        finally { TryDeleteFile(temporary); }
    }

    private async Task CopyDatabaseAsync(string destination, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        await using var target = new SqliteConnection($"Data Source={destination};Mode=ReadWriteCreate;Pooling=False");
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(target);
    }

    private async Task CopyManagedFilesAsync(string stagingDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.ProjectsDirectory)) return;
        foreach (var source in Directory.EnumerateFiles(paths.ProjectsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(paths.ProjectsDirectory, source);
            var destination = Path.Combine(stagingDirectory, "projects", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private async Task WriteStatusAsync(BackupStatus status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var temporary = paths.BackupStatusPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
            File.Move(temporary, paths.BackupStatusPath, true);
        }
        finally { TryDeleteFile(temporary); }
    }

    private void ApplyRetention(string currentArchive)
    {
        var keep = Math.Max(1, retentionCount);
        foreach (var old in Directory.EnumerateFiles(paths.BackupsDirectory, "GeoAutoDocs_*.zip")
                     .OrderByDescending(File.GetCreationTimeUtc).ThenByDescending(x => x)
                     .Where(x => !string.Equals(x, currentArchive, StringComparison.OrdinalIgnoreCase))
                     .Skip(keep - 1))
            TryDeleteFile(old);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
