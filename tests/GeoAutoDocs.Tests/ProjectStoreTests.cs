using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class ProjectStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsTests", Guid.NewGuid().ToString("N"));
    private ProjectStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new ProjectStore(new AppDataPaths(_root));
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreatesAndListsProject()
    {
        var created = await _store.CreateAsync("Teste");
        var projects = await _store.ListAsync();
        Assert.Single(projects);
        Assert.Equal(created.Id, projects[0].Id);
        Assert.Equal("Teste", projects[0].Name);
    }

    [Fact]
    public async Task ReportsReceiveIndependentSnapshotsOfCurrentDefaults()
    {
        var project = await _store.CreateAsync("Buckman");
        project.DefaultClient = "Buckman Lab.";
        project.DefaultCnpj = "1201";
        project.DefaultContract = "105";
        var report001 = project.CreateReport();
        project.DefaultContract = "118";
        var report002 = project.CreateReport();
        report002.Client = "Cliente exclusivo";

        Assert.Equal("105", report001.Contract);
        Assert.Equal("118", report002.Contract);
        Assert.Equal("Buckman Lab.", project.DefaultClient);
        Assert.Equal("Buckman Lab.", report001.Client);
        Assert.Equal("Cliente exclusivo", report002.Client);

        await _store.SaveAsync(project);
        var recovered = await _store.GetAsync(project.Id);
        Assert.Equal(2, recovered!.Reports.Count);
        Assert.Equal(new[] { "105", "118" }, recovered.Reports.OrderBy(x => x.Order).Select(x => x.Contract));
    }

    [Fact]
    public async Task SameSectionCanBelongToMultipleReportsWithoutDuplication()
    {
        var project = await _store.CreateAsync("Trechos");
        var location = new Location { ProjectId = project.Id, Name = "PV01" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho 01" };
        location.Sections.Add(section); project.Locations.Add(location);
        var first = project.CreateReport(); var second = project.CreateReport();
        first.SelectSection(section); second.SelectSection(section);
        await _store.SaveAsync(project);

        var recovered = await _store.GetAsync(project.Id);
        Assert.Single(recovered!.Locations[0].Sections);
        Assert.All(recovered.Reports, x => Assert.Equal(section.Id, Assert.Single(x.SectionLinks).SectionId));
    }

    [Fact]
    public async Task EditingSectionFieldsPreservesItsIdentityAndReportLink()
    {
        var project = await _store.CreateAsync("Edição");
        var location = new Location { ProjectId = project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho" };
        location.Sections.Add(section);
        project.Locations.Add(location);
        project.CreateReport().SelectSection(section);
        await _store.SaveAsync(project);

        section.Name = "Trecho editado";
        section.StartPoint = "PV 01";
        section.EndPoint = null;
        section.Diameter = 9.5m;
        section.Reference = "REF";
        section.Description = "Descrição";
        section.InvestigatedMeters = 9.25m;
        await _store.SaveAsync(project);

        var recovered = await _store.GetAsync(project.Id);
        var edited = Assert.Single(Assert.Single(recovered!.Locations).Sections);
        Assert.Equal(section.Id, edited.Id);
        Assert.Equal((9.5m, 9.25m), (edited.Diameter, edited.InvestigatedMeters));
        Assert.Equal(section.Id, Assert.Single(Assert.Single(recovered.Reports).SectionLinks).SectionId);
    }

    [Fact]
    public async Task RemovingLocationDeletesItsCompleteInspectionAggregate()
    {
        var project = await _store.CreateAsync("Exclusão");
        var captureDirectory = Path.Combine(project.ProjectPath!, "captures");
        Directory.CreateDirectory(captureDirectory);
        var capturePath = Path.Combine(captureDirectory, "capture.png");
        await File.WriteAllTextAsync(capturePath, "managed");
        var location = new Location { ProjectId = project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho" };
        var video = new VideoReference { InspectionSectionId = section.Id, FilePath = @"C:\video.mp4" };
        var capture = new Capture
        {
            ProjectId = project.Id, SectionId = section.Id, VideoReferenceId = video.Id,
            FilePath = capturePath, Status = CaptureStatus.Assigned, ManagedByGeoAutoDocs = true
        };
        var occurrence = new Occurrence { InspectionSectionId = section.Id, VideoReferenceId = video.Id };
        occurrence.Evidence.Add(new Evidence
        {
            OccurrenceId = occurrence.Id, CaptureId = capture.Id, FilePath = capture.FilePath,
            SourceType = EvidenceSourceType.VideoCapture
        });
        section.VideoReferences.Add(video);
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        project.Captures.Add(capture);
        project.CreateReport().SelectSection(section);
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!;
        reopened.Reports[0].SectionLinks.Clear();
        reopened.Captures.Clear();
        reopened.Locations.Clear();
        await _store.SaveAsync(reopened);

        var saved = (await _store.GetAsync(project.Id))!;
        Assert.Empty(saved.Locations);
        Assert.Empty(saved.Captures);
        Assert.Empty(saved.Reports[0].SectionLinks);
        Assert.False(File.Exists(capturePath));
    }

    [Fact]
    public async Task PersistsOrdersAndRemovesOnlySelectedVideoReference()
    {
        var project = await _store.CreateAsync("Mídias");
        var location = new Location { ProjectId = project.Id, Name = "PV01" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho 01" };
        section.VideoReferences.AddRange([
            new VideoReference { InspectionSectionId = section.Id, FilePath = @"C:\videos\ausente.mp4", DisplayName = "Principal", Order = 0 },
            new VideoReference { InspectionSectionId = section.Id, FilePath = @"\\SERVIDOR\videos\continuacao.mp4", DisplayName = "Continuação", Order = 1 },
            new VideoReference { InspectionSectionId = section.Id, FilePath = @"D:\reinspecao.mp4", DisplayName = "Reinspeção", Order = 2 }
        ]);
        var removedId = section.VideoReferences[1].Id;
        section.Occurrences.Add(new Occurrence { InspectionSectionId = section.Id, VideoReferenceId = section.VideoReferences[2].Id, VideoTimestamp = TimeSpan.FromSeconds(12) });
        location.Sections.Add(section); project.Locations.Add(location);
        await _store.SaveAsync(project);

        var firstRestart = await new ProjectStore(new AppDataPaths(_root)).GetAsync(project.Id);
        var videos = firstRestart!.Locations[0].Sections[0].VideoReferences.OrderBy(x => x.Order).ToList();
        Assert.Equal(3, videos.Count);
        Assert.Equal(MediaReferenceStatus.LocalOnly, videos[0].Status);
        Assert.Equal(MediaReferenceStatus.SharedUnc, videos[1].Status);
        Assert.Equal(videos[2].Id, firstRestart.Locations[0].Sections[0].Occurrences[0].VideoReferenceId);

        firstRestart.Locations[0].Sections[0].VideoReferences.RemoveAll(x => x.Id == removedId);
        await _store.SaveAsync(firstRestart);
        var secondRestart = await _store.GetAsync(project.Id);
        var remaining = secondRestart!.Locations[0].Sections[0].VideoReferences.OrderBy(x => x.Order).ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(new[] { 0, 2 }, remaining.Select(x => x.Order));
        Assert.Contains(remaining, x => x.FilePath.EndsWith("ausente.mp4"));
    }

    [Fact]
    public async Task BlankLegacyReportIsPredictablyMigratedToReport001WithoutDataLoss()
    {
        var project = await _store.CreateAsync("Legado");
        var report = project.CreateReport("");
        report.Name = "";
        report.Client = "Cliente legado";
        await _store.SaveAsync(project);

        var dbPath = new AppDataPaths(_root).DatabasePath;
        await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ReportConfiguration SET Name = '' WHERE Id = $id";
            command.Parameters.AddWithValue("$id", report.Id);
            await command.ExecuteNonQueryAsync();
        }

        var restarted = new ProjectStore(new AppDataPaths(_root));
        await restarted.InitializeAsync();
        var recovered = await restarted.GetAsync(project.Id);
        var migrated = Assert.Single(recovered!.Reports);
        Assert.Equal("Report 001", migrated.Name);
        Assert.Equal("Cliente legado", migrated.Client);
    }

    [Fact]
    public async Task SchemaUsesMigrationCompatibleReportSectionTableName()
    {
        var dbPath = new AppDataPaths(_root).DatabasePath;
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'ReportSection%'";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Contains("ReportSection", tables);
        Assert.DoesNotContain("ReportSections", tables);
    }

    [Fact]
    public async Task DeletesProjectAndAssociatedData()
    {
        var project = await _store.CreateAsync("Excluir");
        var projectPath = project.ProjectPath!;
        var externalFile = Path.Combine(projectPath, "video-externo.mp4");
        await File.WriteAllTextAsync(externalFile, "external");
        var location = new Location { ProjectId = project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho" };
        var video = new VideoReference { InspectionSectionId = section.Id, FilePath = externalFile };
        var captureDirectory = Path.Combine(projectPath, "captures");
        Directory.CreateDirectory(captureDirectory);
        var capturePath = Path.Combine(captureDirectory, "managed.png");
        await File.WriteAllTextAsync(capturePath, "managed");
        section.VideoReferences.Add(video); location.Sections.Add(section); project.Locations.Add(location);
        project.Captures.Add(new Capture { ProjectId = project.Id, SectionId = section.Id,
            VideoReferenceId = video.Id, FilePath = capturePath, ManagedByGeoAutoDocs = true });
        project.CreateReport();
        await _store.SaveAsync(project);
        await _store.DeleteAsync(project.Id);
        Assert.Null(await _store.GetAsync(project.Id));
        Assert.Empty(await _store.ListAsync());
        Assert.False(File.Exists(capturePath));
        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    public async Task FailedAggregateSaveRollsBackPriorDeletions()
    {
        var project = await _store.CreateAsync("Transacional");
        var first = project.CreateReport();
        var second = project.CreateReport();
        second.Client = "Cliente preservado";
        await _store.SaveAsync(project);

        project.Reports.Remove(first);
        second.Client = null!;
        await Assert.ThrowsAsync<DbUpdateException>(() => _store.SaveAsync(project));

        var recovered = await _store.GetAsync(project.Id);
        Assert.Equal(2, recovered!.Reports.Count);
        Assert.Equal("Cliente preservado", recovered.Reports.Single(x => x.Id == second.Id).Client);
    }
    [Fact]
    public async Task FocusedSectionSavePersistsMetadataWithoutTouchingRelatedAggregate()
    {
        var project = await _store.CreateAsync("Persistência focada");
        var location = new Location { ProjectId = project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho original" };
        var video = new VideoReference
        {
            InspectionSectionId = section.Id,
            FilePath = @"C:\videos\inspecao.mp4",
            DisplayName = "Inspeção"
        };
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            VideoReferenceId = video.Id,
            Meterage = 4.25m
        };
        occurrence.Evidence.Add(new Evidence
        {
            OccurrenceId = occurrence.Id,
            FilePath = @"C:\evidencias\frame.png",
            SourceType = EvidenceSourceType.ExternalImage
        });
        section.VideoReferences.Add(video);
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        project.CreateReport().SelectSection(section);
        await _store.SaveAsync(project);

        section.Name = "Trecho editado";
        section.Description = "Descrição focada";
        section.StartPoint = "PV 01";
        section.EndPoint = "PV 02";
        section.Diameter = 600m;
        section.Reference = "REF-01";
        section.InvestigatedMeters = 18.75m;
        await _store.SaveSectionAsync(section);

        var recovered = await _store.GetAsync(project.Id);
        var savedSection = Assert.Single(Assert.Single(recovered!.Locations).Sections);
        Assert.Equal("Trecho editado", savedSection.Name);
        Assert.Equal("Descrição focada", savedSection.Description);
        Assert.Equal(("PV 01", "PV 02", 600m, "REF-01", 18.75m),
            (savedSection.StartPoint, savedSection.EndPoint, savedSection.Diameter,
                savedSection.Reference, savedSection.InvestigatedMeters));
        Assert.Single(savedSection.VideoReferences);
        Assert.Single(Assert.Single(savedSection.Occurrences).Evidence);
        Assert.Equal(section.Id, Assert.Single(Assert.Single(recovered.Reports).SectionLinks).SectionId);
    }

    [Fact]
    public async Task ConcurrentAggregateAndFocusedSectionSavesPreserveBothChanges()
    {
        var project = await _store.CreateAsync("Serialização");
        var location = new Location { ProjectId = project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Nome original" };
        location.Sections.Add(section);
        project.Locations.Add(location);
        project.CreateReport();
        await _store.SaveAsync(project);

        var aggregateSnapshot = (await _store.GetAsync(project.Id))!;
        var focusedSnapshot = (await _store.GetAsync(project.Id))!;
        aggregateSnapshot.Reports.Single().Client = "Cliente atualizado";
        focusedSnapshot.Locations.Single().Sections.Single().Name = "Nome atualizado";

        var aggregateSave = _store.SaveAsync(aggregateSnapshot);
        var focusedSave = _store.SaveSectionAsync(focusedSnapshot.Locations.Single().Sections.Single());
        await Task.WhenAll(aggregateSave, focusedSave);

        var recovered = await _store.GetAsync(project.Id);
        Assert.Equal("Cliente atualizado", Assert.Single(recovered!.Reports).Client);
        Assert.Equal("Nome atualizado", Assert.Single(Assert.Single(recovered.Locations).Sections).Name);
    }

}
