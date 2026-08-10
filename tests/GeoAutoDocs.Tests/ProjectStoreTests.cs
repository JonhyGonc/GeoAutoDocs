using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
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
        Assert.NotEqual(Guid.Empty, created.Id);
    }

    [Fact]
    public async Task UpdatesAndRecoversUsingNewStoreInstance()
    {
        var project = await _store.CreateAsync("Teste");
        var originalUpdatedAt = project.UpdatedAt;
        project.ReportConfiguration.Client = "Cliente preservado";
        project.ReportConfiguration.Area = "Área preservada";
        await Task.Delay(10);
        await _store.SaveAsync(project);

        var reopenedStore = new ProjectStore(new AppDataPaths(_root));
        await reopenedStore.InitializeAsync();
        var recovered = await reopenedStore.GetAsync(project.Id);

        Assert.NotNull(recovered);
        Assert.Equal("Cliente preservado", recovered.ReportConfiguration.Client);
        Assert.Equal("Área preservada", recovered.ReportConfiguration.Area);
        Assert.True(recovered.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public async Task PersistsLocationSectionAndVideoReference()
    {
        var project = await _store.CreateAsync("Hierarquia");
        var location = new Location { ProjectId = project.Id, Name = "PV01", Type = LocationType.Manhole };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho 01", Diameter = 600 };
        section.VideoReference = new VideoReference
        {
            InspectionSectionId = section.Id,
            FilePath = @"C:\videos\trecho01.mp4"
        };
        location.Sections.Add(section);
        project.Locations.Add(location);
        await _store.SaveAsync(project);

        var recovered = await _store.GetAsync(project.Id);
        Assert.Equal("PV01", Assert.Single(recovered!.Locations).Name);
        Assert.Equal(600, Assert.Single(recovered.Locations[0].Sections).Diameter);
        Assert.Equal(@"C:\videos\trecho01.mp4", recovered.Locations[0].Sections[0].VideoReference!.FilePath);
    }

    [Fact]
    public async Task OccurrenceSupportsMultipleAnomalies()
    {
        var project = await _store.CreateAsync("Anomalias");
        var location = new Location { ProjectId = project.Id, Name = "Local" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho" };
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            LocationMode = OccurrenceLocationMode.SpecificMeterage,
            Meterage = 18
        };
        occurrence.Anomalies.Add(new AnomalyDefinition { Name = "Trinca", Order = 1 });
        occurrence.Anomalies.Add(new AnomalyDefinition { Name = "Infiltração", Order = 2 });
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        await _store.SaveAsync(project);

        var recovered = await _store.GetAsync(project.Id);
        var anomalies = recovered!.Locations[0].Sections[0].Occurrences[0].Anomalies;
        Assert.Equal(2, anomalies.Count);
        Assert.Contains(anomalies, x => x.Name == "Trinca");
        Assert.Contains(anomalies, x => x.Name == "Infiltração");
    }

    [Fact]
    public async Task DeletesProjectAndAssociatedData()
    {
        var project = await _store.CreateAsync("Excluir");
        project.Locations.Add(new Location { ProjectId = project.Id, Name = "Local" });
        await _store.SaveAsync(project);

        await _store.DeleteAsync(project.Id);

        Assert.Null(await _store.GetAsync(project.Id));
        Assert.Empty(await _store.ListAsync());
    }
}
