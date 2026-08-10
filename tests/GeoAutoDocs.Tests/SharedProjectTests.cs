using System.Text.Json;
using System.Text.Json.Nodes;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class SharedProjectTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsSharedTests", Guid.NewGuid().ToString("N"));
    private AppDataPaths _paths = null!;
    private Project _project = null!;

    public Task InitializeAsync()
    {
        _paths = new AppDataPaths(Path.Combine(_root, "local"));
        _paths.EnsureDirectories();
        _project = new Project { Id = Guid.NewGuid(), Name = "Compartilhado", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _project.CreateReport();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LockAllowsOneEditorAndReadOnlySessionCannotCheckpoint()
    {
        var directory = Path.Combine(_root, "servidor");
        var service = new SharedProjectService(_paths);
        var editor = await service.OpenAsync(_project, directory);
        Assert.True(File.Exists(Path.Combine(directory, "lock.json")));

        var error = await Assert.ThrowsAsync<SharedProjectLockedException>(() => service.OpenAsync(_project, directory));
        Assert.False(string.IsNullOrWhiteSpace(error.ProjectLock.UserName));
        var reader = await service.OpenAsync(_project, directory, readOnly: true);
        Assert.True(reader.IsReadOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.CheckpointAsync());

        await editor.CloseAsync();
        Assert.False(File.Exists(Path.Combine(directory, "lock.json")));
    }

    [Fact]
    public async Task HeartbeatUpdatesOwnedLock()
    {
        var directory = Path.Combine(_root, "heartbeat");
        var service = new SharedProjectService(_paths);
        var session = await service.OpenAsync(_project, directory);
        var before = File.GetLastWriteTimeUtc(Path.Combine(directory, "lock.json"));
        await Task.Delay(30);
        await session.HeartbeatAsync();
        var after = File.GetLastWriteTimeUtc(Path.Combine(directory, "lock.json"));
        Assert.True(after >= before);
        await session.CloseAsync();
    }

    [Fact]
    public async Task StaleLockCanBeRecoveredExplicitly()
    {
        var directory = Path.Combine(_root, "stale");
        var service = new SharedProjectService(_paths);
        _ = await service.OpenAsync(_project, directory);
        var lockPath = Path.Combine(directory, "lock.json");
        var stale = new ProjectLock(_project.Id, Guid.NewGuid(), "João", "PC-ANTIGO",
            DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1));
        await File.WriteAllTextAsync(lockPath, JsonSerializer.Serialize(stale));

        await Assert.ThrowsAsync<SharedProjectLockedException>(() =>
            service.OpenAsync(_project, directory, staleAfter: TimeSpan.FromMinutes(5)));
        var recovered = await service.OpenAsync(_project, directory, recoverStaleLock: true,
            staleAfter: TimeSpan.FromMinutes(5));
        Assert.False(recovered.IsReadOnly);
        await recovered.CloseAsync();
    }

    [Fact]
    public async Task CachePreservesWorkWhenServerBecomesUnavailable()
    {
        var directory = Path.Combine(_root, "online");
        var offline = Path.Combine(_root, "offline");
        var service = new SharedProjectService(_paths);
        var session = await service.OpenAsync(_project, directory);
        session.Project.DefaultContract = "ALTERAÇÃO PROTEGIDA";
        Directory.Move(directory, offline);

        await Assert.ThrowsAnyAsync<Exception>(() => session.CheckpointAsync());
        Assert.True(File.Exists(session.LocalCachePath));
        using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(session.LocalCachePath));
        Assert.Equal("ALTERAÇÃO PROTEGIDA", cached.RootElement.GetProperty("DefaultContract").GetString());
    }

    [Fact]
    public async Task UnexpectedServerVersionIsNeverOverwrittenSilently()
    {
        var directory = Path.Combine(_root, "version");
        var service = new SharedProjectService(_paths);
        var session = await service.OpenAsync(_project, directory);
        var officialPath = Path.Combine(directory, "project.geoadocs.json");
        var official = JsonNode.Parse(await File.ReadAllTextAsync(officialPath))!.AsObject();
        official["ProjectDataVersion"] = 99;
        await File.WriteAllTextAsync(officialPath, official.ToJsonString());

        var error = await Assert.ThrowsAsync<ProjectVersionConflictException>(() => session.CheckpointAsync());
        Assert.Equal(99, error.Actual);
        Assert.Contains("encontrado 99", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagedCaptureCheckpointsToServerAndRestoresIntoAnotherLocalCache()
    {
        var directory = Path.Combine(_root, "capture-sync");
        var sourcePng = Path.Combine(_root, "capture.png");
        await File.WriteAllBytesAsync(sourcePng, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFElEQVR42mP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"));
        var location = new Location { ProjectId = _project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho" };
        var video = new VideoReference { InspectionSectionId = section.Id, FilePath = @"C:\video.mp4", DisplayName = "Vídeo" };
        section.VideoReferences.Add(video); location.Sections.Add(section); _project.Locations.Add(location);
        var capture = new Capture
        {
            ProjectId = _project.Id, SectionId = section.Id, VideoReferenceId = video.Id,
            FilePath = sourcePng, VideoTimestamp = TimeSpan.FromSeconds(3), CreatedAt = DateTime.UtcNow,
            Status = CaptureStatus.Pending, ManagedByGeoAutoDocs = true
        };
        _project.Captures.Add(capture);
        var service = new SharedProjectService(_paths);
        var editor = await service.OpenAsync(_project, directory);

        await editor.CheckpointAsync();
        Assert.True(File.Exists(editor.Project.Captures[0].SharedFilePath));
        await editor.CloseAsync();

        var secondPaths = new AppDataPaths(Path.Combine(_root, "other-local"));
        secondPaths.EnsureDirectories();
        var reader = await new SharedProjectService(secondPaths).OpenAsync(
            new Project { Id = _project.Id, Name = _project.Name }, directory, readOnly: true);
        Assert.True(File.Exists(reader.Project.Captures[0].FilePath));
        Assert.StartsWith(secondPaths.CacheDirectory, reader.Project.Captures[0].FilePath, StringComparison.OrdinalIgnoreCase);
        await reader.CloseAsync();
    }
}
