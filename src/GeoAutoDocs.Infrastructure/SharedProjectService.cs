using System.Text.Json;
using System.Text.Json.Serialization;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed record ProjectLock(
    Guid ProjectId, Guid SessionId, string UserName, string ComputerName,
    DateTime OpenedAt, DateTime LastHeartbeat);

public sealed class SharedProjectLockedException(ProjectLock projectLock)
    : InvalidOperationException($"Projeto em edição por {projectLock.UserName} em {projectLock.ComputerName}.")
{
    public ProjectLock ProjectLock { get; } = projectLock;
}

public sealed class ProjectVersionConflictException(long expected, long actual)
    : InvalidOperationException($"O projeto compartilhado mudou no servidor (esperado {expected}, encontrado {actual}).")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

public sealed class SharedProjectService(AppDataPaths paths)
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);
    private const string ProjectFileName = "project.geoadocs.json";
    private const string LockFileName = "lock.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.Preserve
    };

    public async Task<SharedProjectSession> OpenAsync(Project project, string sharedDirectory,
        bool readOnly = false, bool recoverStaleLock = false, TimeSpan? staleAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sharedDirectory)) throw new ArgumentException("A pasta compartilhada é obrigatória.");
        sharedDirectory = Path.GetFullPath(sharedDirectory);
        Directory.CreateDirectory(sharedDirectory);
        var officialPath = Path.Combine(sharedDirectory, ProjectFileName);
        var lockPath = Path.Combine(sharedDirectory, LockFileName);
        var cacheDirectory = Path.Combine(paths.CacheDirectory, project.Id.ToString("N"));
        var cachePath = Path.Combine(cacheDirectory, ProjectFileName);
        Directory.CreateDirectory(cacheDirectory);

        ProjectLock? ownedLock = null;
        if (!readOnly)
        {
            var existing = await ReadLockAsync(lockPath, cancellationToken);
            if (existing is not null)
            {
                var isStale = DateTime.UtcNow - existing.LastHeartbeat > (staleAfter ?? DefaultStaleAfter);
                if (!isStale || !recoverStaleLock) throw new SharedProjectLockedException(existing);
                File.Delete(lockPath);
            }

            ownedLock = new ProjectLock(project.Id, Guid.NewGuid(), Environment.UserName,
                Environment.MachineName, DateTime.UtcNow, DateTime.UtcNow);
            await CreateLockAtomicallyAsync(lockPath, ownedLock, cancellationToken);
        }

        try
        {
            Project working;
            if (File.Exists(officialPath))
            {
                working = await ReadProjectAsync(officialPath, cancellationToken);
                if (working.Id != project.Id) throw new InvalidDataException("A pasta contém outro projeto.");
            }
            else
            {
                if (readOnly) throw new FileNotFoundException("Projeto compartilhado não encontrado.", officialPath);
                project.StorageKind = ProjectStorageKind.Shared;
                project.SharedProjectPath = sharedDirectory;
                project.ProjectDataVersion = Math.Max(1, project.ProjectDataVersion);
                await WriteProjectAtomicallyAsync(officialPath, project, cancellationToken);
                working = project;
            }

            working.StorageKind = ProjectStorageKind.Shared;
            working.SharedProjectPath = sharedDirectory;
            await RestoreCapturesToCacheAsync(working, cacheDirectory, cancellationToken);
            await WriteProjectAtomicallyAsync(cachePath, working, cancellationToken);
            return new SharedProjectSession(this, working, officialPath, cachePath, lockPath,
                ownedLock, readOnly, working.ProjectDataVersion);
        }
        catch
        {
            if (ownedLock is not null) TryDeleteOwnedLock(lockPath, ownedLock.SessionId);
            throw;
        }
    }

    internal async Task CheckpointAsync(SharedProjectSession session, CancellationToken cancellationToken)
    {
        await WriteProjectAtomicallyAsync(session.CachePath, session.Project, cancellationToken);
        if (session.IsReadOnly) throw new InvalidOperationException("A sessão está em modo somente leitura.");
        EnsureOwnedLock(session);
        var official = await ReadProjectAsync(session.OfficialPath, cancellationToken);
        if (official.ProjectDataVersion != session.BaseVersion)
            throw new ProjectVersionConflictException(session.BaseVersion, official.ProjectDataVersion);
        session.Project.ProjectDataVersion = session.BaseVersion + 1;
        await SynchronizeCapturesToSharedAsync(session.Project,
            Path.GetDirectoryName(session.OfficialPath)!, cancellationToken);
        await WriteProjectAtomicallyAsync(session.OfficialPath, session.Project, cancellationToken);
        session.BaseVersion = session.Project.ProjectDataVersion;
        await WriteProjectAtomicallyAsync(session.CachePath, session.Project, cancellationToken);
    }

    internal async Task SaveCacheAsync(SharedProjectSession session, CancellationToken cancellationToken) =>
        await WriteProjectAtomicallyAsync(session.CachePath, session.Project, cancellationToken);

    internal async Task HeartbeatAsync(SharedProjectSession session, CancellationToken cancellationToken)
    {
        if (session.IsReadOnly || session.OwnedLock is null) return;
        EnsureOwnedLock(session);
        session.OwnedLock = session.OwnedLock with { LastHeartbeat = DateTime.UtcNow };
        await WriteLockAtomicallyAsync(session.LockPath, session.OwnedLock, cancellationToken);
    }

    internal void ReleaseLock(SharedProjectSession session)
    {
        if (session.OwnedLock is null) return;
        EnsureOwnedLock(session);
        File.Delete(session.LockPath);
        session.OwnedLock = null;
    }

    private static void EnsureOwnedLock(SharedProjectSession session)
    {
        if (session.OwnedLock is null) throw new InvalidOperationException("A sessão não possui lock de edição.");
        var current = ReadLockAsync(session.LockPath, CancellationToken.None).GetAwaiter().GetResult();
        if (current?.SessionId != session.OwnedLock.SessionId)
            throw new InvalidOperationException("O lock de edição não pertence mais a esta sessão.");
    }

    private static async Task<Project> ReadProjectAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<Project>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Estado de projeto inválido.");
    }

    private static async Task<ProjectLock?> ReadLockAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<ProjectLock>(stream, JsonOptions, cancellationToken);
    }

    private static async Task CreateLockAtomicallyAsync(string path, ProjectLock projectLock, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, projectLock, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteLockAtomicallyAsync(string path, ProjectLock projectLock, CancellationToken cancellationToken) =>
        await AtomicWriteAsync(path, projectLock, cancellationToken);

    private static async Task WriteProjectAtomicallyAsync(string path, Project project, CancellationToken cancellationToken) =>
        await AtomicWriteAsync(path, project, cancellationToken);

    private static async Task SynchronizeCapturesToSharedAsync(Project project, string sharedDirectory,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(sharedDirectory, "captures");
        foreach (var capture in project.Captures.Where(x => x.ManagedByGeoAutoDocs && File.Exists(x.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetDirectory);
            var target = Path.Combine(targetDirectory, $"capture_{capture.Id:N}.png");
            if (ShouldCopyManagedFile(capture.FilePath, target))
                await CopyAtomicallyAsync(capture.FilePath, target, cancellationToken);
            capture.SharedFilePath = target;
        }

        var evidenceDirectory = Path.Combine(sharedDirectory, "evidence");
        foreach (var evidence in project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
                     .SelectMany(x => x.Evidence).Where(x => x.SourceType == EvidenceSourceType.ExternalImage &&
                         x.ManagedByGeoAutoDocs && File.Exists(x.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(evidenceDirectory);
            var target = Path.Combine(evidenceDirectory, $"evidence_{evidence.Id:N}{Path.GetExtension(evidence.FilePath).ToLowerInvariant()}");
            if (ShouldCopyManagedFile(evidence.FilePath, target))
                await CopyAtomicallyAsync(evidence.FilePath, target, cancellationToken);
            evidence.SharedFilePath = target;
        }
    }

    private static async Task RestoreCapturesToCacheAsync(Project project, string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var captureDirectory = Path.Combine(cacheDirectory, "captures");
        foreach (var capture in project.Captures.Where(x => x.ManagedByGeoAutoDocs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = Path.Combine(captureDirectory, $"capture_{capture.Id:N}.png");
            if (File.Exists(local))
            {
                capture.FilePath = local;
                continue;
            }
            if (string.IsNullOrWhiteSpace(capture.SharedFilePath) || !File.Exists(capture.SharedFilePath)) continue;
            Directory.CreateDirectory(captureDirectory);
            await CopyAtomicallyAsync(capture.SharedFilePath, local, cancellationToken);
            capture.FilePath = local;
        }

        var captures = project.Captures.ToDictionary(x => x.Id);
        var evidenceDirectory = Path.Combine(cacheDirectory, "evidence");
        foreach (var evidence in project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences).SelectMany(x => x.Evidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence.CaptureId is Guid captureId && captures.TryGetValue(captureId, out var capture))
            {
                evidence.FilePath = capture.FilePath;
                evidence.VideoReferenceId = capture.VideoReferenceId;
                evidence.VideoTimestamp = capture.VideoTimestamp;
                continue;
            }
            if (evidence.SourceType != EvidenceSourceType.ExternalImage || !evidence.ManagedByGeoAutoDocs) continue;
            var local = Path.Combine(evidenceDirectory, $"evidence_{evidence.Id:N}{Path.GetExtension(evidence.FilePath).ToLowerInvariant()}");
            if (!File.Exists(local) && !string.IsNullOrWhiteSpace(evidence.SharedFilePath) && File.Exists(evidence.SharedFilePath))
            {
                Directory.CreateDirectory(evidenceDirectory);
                await CopyAtomicallyAsync(evidence.SharedFilePath, local, cancellationToken);
            }
            if (File.Exists(local)) evidence.FilePath = local;
        }
    }

    private static bool ShouldCopyManagedFile(string source, string destination)
    {
        if (PathsEqual(source, destination)) return false;
        var destinationInfo = new FileInfo(destination);
        if (!destinationInfo.Exists) return true;

        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length != destinationInfo.Length) return true;
        return sourceInfo.LastWriteTimeUtc > destinationInfo.LastWriteTimeUtc;
    }

    private static async Task CopyAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        var sourceLastWriteTime = File.GetLastWriteTimeUtc(source);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, true);
            File.SetLastWriteTimeUtc(destination, sourceLastWriteTime);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task AtomicWriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            await using (var validation = File.OpenRead(temporary))
                _ = await JsonSerializer.DeserializeAsync<T>(validation, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("Checkpoint inválido.");
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void TryDeleteOwnedLock(string path, Guid sessionId)
    {
        try
        {
            var current = ReadLockAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            if (current?.SessionId == sessionId) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class SharedProjectSession
{
    private readonly SharedProjectService _service;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private bool _closed;
    internal string OfficialPath { get; }
    internal string CachePath { get; }
    internal string LockPath { get; }
    internal ProjectLock? OwnedLock { get; set; }
    internal long BaseVersion { get; set; }
    public Project Project { get; }
    public bool IsReadOnly { get; }
    public bool IsClosed => _closed;
    public string LocalCachePath => CachePath;

    internal SharedProjectSession(SharedProjectService service, Project project, string officialPath,
        string cachePath, string lockPath, ProjectLock? ownedLock, bool isReadOnly, long baseVersion)
    {
        _service = service;
        Project = project;
        OfficialPath = officialPath;
        CachePath = cachePath;
        LockPath = lockPath;
        OwnedLock = ownedLock;
        IsReadOnly = isReadOnly;
        BaseVersion = baseVersion;
    }

    public Task SaveLocalAsync(CancellationToken cancellationToken = default) =>
        _service.SaveCacheAsync(this, cancellationToken);

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _checkpointGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosed();
            await _service.CheckpointAsync(this, cancellationToken);
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        await _checkpointGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosed();
            await _service.HeartbeatAsync(this, cancellationToken);
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _checkpointGate.WaitAsync(cancellationToken);
        try
        {
            if (_closed) return;
            if (!IsReadOnly) await _service.CheckpointAsync(this, cancellationToken);
            _service.ReleaseLock(this);
            _closed = true;
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public async Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        await _checkpointGate.WaitAsync(cancellationToken);
        try
        {
            if (_closed) return;
            _service.ReleaseLock(this);
            _closed = true;
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new InvalidOperationException("A sessão compartilhada já foi finalizada.");
    }
}
