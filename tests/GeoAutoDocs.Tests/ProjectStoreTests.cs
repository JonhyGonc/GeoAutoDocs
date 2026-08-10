using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Data.Sqlite;
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
        project.CreateReport();
        await _store.SaveAsync(project);
        await _store.DeleteAsync(project.Id);
        Assert.Null(await _store.GetAsync(project.Id));
        Assert.Empty(await _store.ListAsync());
    }
}
