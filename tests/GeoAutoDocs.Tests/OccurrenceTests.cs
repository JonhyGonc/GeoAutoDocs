using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class OccurrenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsOccurrenceTests", Guid.NewGuid().ToString("N"));
    private AppDataPaths _paths = null!;
    private ProjectStore _store = null!;
    private OccurrenceService _service = null!;
    private IReadOnlyList<AnomalyDefinition> _catalog = null!;
    private Guid _sectionId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(_root);
        _store = new ProjectStore(_paths);
        await _store.InitializeAsync();
        _service = new OccurrenceService(_paths, new LocalLogger(_paths.LogPath));
        _catalog = await _store.ListAnomalyDefinitionsAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public void PointAtZeroIsValid()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 0m, "Trinca");
        OccurrenceRules.Validate(occurrence);
        Assert.Equal(0m, occurrence.Meterage);
    }

    [Fact]
    public async Task PointAtEighteenPersists()
    {
        var (project, section) = await CreateProjectAsync();
        await _service.SaveAsync(project, section, NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca"), false);
        await _store.SaveAsync(project);
        var reopened = await _store.GetAsync(project.Id);
        Assert.Equal(18m, reopened!.Locations[0].Sections[0].Occurrences.Single().Meterage);
    }

    [Fact]
    public async Task RangePersists()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Range, 18m, "Assoreamento");
        occurrence.EndMeterage = 24.5m;
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal((18m, 24.5m), (saved.Meterage, saved.EndMeterage));
    }

    [Fact]
    public void InvertedRangeIsRejected()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Range, 24.5m, "Assoreamento");
        occurrence.EndMeterage = 18m;
        Assert.Throws<InvalidOperationException>(() => OccurrenceRules.Validate(occurrence));
    }

    [Fact]
    public async Task EntireSectionDoesNotCreateArtificialMeasurement()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.EntireSection, 99m, "Deformação");
        occurrence.EndMeterage = 120m;
        var saved = await _service.SaveAsync(project, section, occurrence, false);
        Assert.Null(saved.Meterage);
        Assert.Null(saved.EndMeterage);
    }

    [Fact]
    public void OneOccurrenceAcceptsMultipleAnomalies()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração");
        OccurrenceRules.Validate(occurrence);
        Assert.Equal(2, occurrence.Anomalies.Count);
    }

    [Fact]
    public void FissureAndCrackRemainDistinctTypes()
    {
        var fissure = Definition("Fissura");
        var crack = Definition("Trinca");
        Assert.NotEqual(fissure.Id, crack.Id);
        Assert.NotEqual(fissure.Name, crack.Name);
    }

    [Fact]
    public async Task CrackAndInfiltrationPersistAsTwoLinksInOneOccurrence()
    {
        var (project, section) = await CreateProjectAsync();
        await _service.SaveAsync(project, section,
            NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração"), false);
        await _store.SaveAsync(project);
        var occurrences = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences;
        Assert.Single(occurrences);
        Assert.Equal(2, occurrences[0].Anomalies.Count);
    }

    [Fact]
    public async Task PositionPersistsPerAnomaly()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração");
        occurrence.Anomalies[0].Position = AnomalyPosition.RightSide;
        occurrence.Anomalies[1].Position = AnomalyPosition.UpperGenerator;
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal([AnomalyPosition.RightSide, AnomalyPosition.UpperGenerator],
            saved.Anomalies.OrderBy(x => x.Order).Select(x => x.Position).ToArray());
    }

    [Fact]
    public void OtherRequiresCustomText()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 25m, "Outro");
        Assert.Throws<InvalidOperationException>(() => OccurrenceRules.Validate(occurrence));
        occurrence.Anomalies[0].CustomText = "Anomalia de teste sem padrão previamente cadastrado.";
        OccurrenceRules.Validate(occurrence);
    }

    [Fact]
    public async Task MultipleEvidencePersistInOrder()
    {
        var (project, section) = await CreateProjectAsync();
        var first = AddCapture(project, section, 1000);
        var second = AddCapture(project, section, 2000);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        occurrence.Evidence.AddRange([OccurrenceService.FromCapture(first), OccurrenceService.FromCapture(second)]);
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal([first.Id, second.Id], saved.Evidence.OrderBy(x => x.Order).Select(x => x.CaptureId!.Value).ToArray());
    }

    [Fact]
    public async Task LinkedCaptureBecomesAssigned()
    {
        var (project, section) = await CreateProjectAsync();
        var capture = AddCapture(project, section, 1000);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        occurrence.Evidence.Add(OccurrenceService.FromCapture(capture));
        await _service.SaveAsync(project, section, occurrence, false);
        Assert.Equal(CaptureStatus.Assigned, capture.Status);
    }

    [Fact]
    public async Task DeletingOccurrenceReturnsCaptureToPending()
    {
        var (project, section) = await CreateProjectAsync();
        var capture = AddCapture(project, section, 1000);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        occurrence.Evidence.Add(OccurrenceService.FromCapture(capture));
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var reopened = (await _store.GetAsync(project.Id))!;
        var reopenedSection = reopened.Locations[0].Sections[0];
        _service.Delete(reopened, reopenedSection, reopenedSection.Occurrences.Single(), false);
        await _store.SaveAsync(reopened);
        var afterDelete = (await _store.GetAsync(project.Id))!;
        Assert.Empty(afterDelete.Locations[0].Sections[0].Occurrences);
        Assert.Equal(CaptureStatus.Pending, afterDelete.Captures.Single().Status);
    }

    [Fact]
    public async Task ReadOnlyBlocksCreateEditAndDelete()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveAsync(project, section, occurrence, true));
        await _service.SaveAsync(project, section, occurrence, false);
        Assert.Throws<InvalidOperationException>(() => _service.Delete(project, section, occurrence, true));
    }

    [Fact]
    public void FormatterIsDeterministic()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração");
        var first = OccurrenceRules.GenerateDescription(occurrence);
        var second = OccurrenceRules.GenerateDescription(occurrence);
        Assert.Equal(first, second);
        Assert.Equal("Na metragem de 18,00 m, observa-se a presença de Trinca e Infiltração.", first);
    }

    [Fact]
    public async Task ManualOverrideIsPreservedWithoutLosingStructuredData()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        occurrence.ManualDescriptionOverride = "Descrição técnica revisada pelo operador.";
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal("Descrição técnica revisada pelo operador.", saved.EffectiveDescription);
        Assert.Single(saved.Anomalies);
        Assert.Equal(18m, saved.Meterage);
    }

    [Fact]
    public async Task OperationalSmokeCreatesEditsAndReopensWithVideoAndExternalEvidence()
    {
        var (project, section) = await CreateProjectAsync();
        var capture = AddCapture(project, section, 28430);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].Position = AnomalyPosition.RightSide;
        occurrence.Evidence.Add(OccurrenceService.FromCapture(capture));
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!;
        var reopenedSection = reopened.Locations[0].Sections[0];
        var draft = _service.CreateDraft(reopenedSection, reopenedSection.Occurrences.Single());
        var external = Path.Combine(_root, "foto-campo.jpg");
        await File.WriteAllBytesAsync(external, TestPng);
        var originalBytes = await File.ReadAllBytesAsync(external);
        draft.Evidence.Add(OccurrenceService.FromExternalImage(external));
        await _service.SaveAsync(reopened, reopenedSection, draft, false);
        await _store.SaveAsync(reopened);

        var final = (await _store.GetAsync(project.Id))!;
        var saved = final.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal(2, saved.Anomalies.Count);
        Assert.Equal(2, saved.Evidence.Count);
        Assert.Equal(CaptureStatus.Assigned, final.Captures.Single().Status);
        Assert.Equal(TimeSpan.FromMilliseconds(28430), saved.Evidence.Single(x => x.CaptureId is not null).VideoTimestamp);
        var externalSaved = saved.Evidence.Single(x => x.SourceType == EvidenceSourceType.ExternalImage);
        Assert.NotEqual(Path.GetFullPath(external), Path.GetFullPath(externalSaved.FilePath));
        Assert.True(File.Exists(externalSaved.FilePath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(external));
    }

    private async Task<(Project Project, InspectionSection Section)> CreateProjectAsync()
    {
        var project = await _store.CreateAsync("Inspeção");
        var location = new Location { ProjectId = project.Id, Name = "RUA DAS FLORES" };
        var section = new InspectionSection { LocationId = location.Id, Name = "TRECHO 01", Diameter = 600 };
        _sectionId = section.Id;
        location.Sections.Add(section);
        project.Locations.Add(location);
        return (project, section);
    }

    private Occurrence NewOccurrence(OccurrenceLocationMode mode, decimal? measurement, params string[] anomalies)
    {
        var occurrence = new Occurrence
        {
            InspectionSectionId = _sectionId, LocationMode = mode, Meterage = measurement,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        occurrence.Anomalies.AddRange(anomalies.Select((name, index) =>
        {
            var definition = Definition(name);
            return new OccurrenceAnomaly
            {
                OccurrenceId = occurrence.Id, AnomalyTypeId = definition.Id,
                AnomalyType = definition, Order = index
            };
        }));
        return occurrence;
    }

    private AnomalyDefinition Definition(string name) => _catalog.Single(x => x.Name == name);

    private Capture AddCapture(Project project, InspectionSection section, int milliseconds)
    {
        var directory = Path.Combine(project.ProjectPath!, "captures");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"capture_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, TestPng);
        var video = section.VideoReferences.FirstOrDefault();
        if (video is null)
        {
            var videoPath = Path.Combine(_root, "video.mp4");
            File.WriteAllBytes(videoPath, [1, 2, 3]);
            video = new VideoReference { InspectionSectionId = section.Id, FilePath = videoPath, DisplayName = "Vídeo" };
            section.VideoReferences.Add(video);
        }
        var capture = new Capture
        {
            ProjectId = project.Id, SectionId = section.Id, VideoReferenceId = video.Id,
            FilePath = file, VideoFilePath = video.FilePath, VideoTimestamp = TimeSpan.FromMilliseconds(milliseconds),
            CreatedAt = DateTime.UtcNow, Status = CaptureStatus.Pending, ManagedByGeoAutoDocs = true,
            Order = project.Captures.Count
        };
        project.Captures.Add(capture);
        return capture;
    }

    private static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFElEQVR42mP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
}
