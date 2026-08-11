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
        Assert.Equal("Trinca e infiltração a 18 metros do início da filmagem.", first);
    }

    [Fact]
    public void FormatterGroupsAnomaliesAtTheSamePosition()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração");
        occurrence.Anomalies.ForEach(x => x.Position = AnomalyPosition.UpperGenerator);

        Assert.Equal("Trinca e infiltração na geratriz superior a 18 metros do início da filmagem.",
            OccurrenceRules.GenerateDescription(occurrence));
    }

    [Fact]
    public void FormatterKeepsEachAnomalyAtItsOwnPosition()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].Position = AnomalyPosition.Side;

        Assert.Equal("Trinca na geratriz superior e infiltração na lateral da tubulação a 18 metros do início da filmagem.",
            OccurrenceRules.GenerateDescription(occurrence));
    }

    [Fact]
    public void FormatterUsesCommasAndFinalConjunctionForThreePositions()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração", "Obstrução");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].Position = AnomalyPosition.Side;
        occurrence.Anomalies[2].Position = AnomalyPosition.LowerGenerator;

        Assert.Equal("Trinca na geratriz superior, infiltração na lateral da tubulação e obstrução na geratriz inferior a 18 metros do início da filmagem.",
            OccurrenceRules.GenerateDescription(occurrence));
    }

    [Fact]
    public void FormatterUsesNaturalContributionLateralWordingAfterGroupedPosition()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca", "Infiltração", "Contribuição");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[2].Position = AnomalyPosition.Side;

        Assert.Equal("Trinca e infiltração na geratriz superior e contribuição lateral a 18 metros do início da filmagem.",
            OccurrenceRules.GenerateDescription(occurrence));
    }

    [Fact]
    public void FormatterRemovesInsignificantMeasurementZerosAndUsesSingular()
    {
        var cases = new[] { (20m, "20 metros"), (18.50m, "18,5 metros"), (18.35m, "18,35 metros"), (1m, "1 metro") };
        foreach (var (measurement, expected) in cases)
        {
            var occurrence = NewOccurrence(OccurrenceLocationMode.Point, measurement, "Trinca");
            Assert.Contains($"a {expected} do início da filmagem", OccurrenceRules.GenerateDescription(occurrence));
        }
    }

    [Fact]
    public void FormatterAddsInterruptionOnlyWhenOperatorMarksIt()
    {
        var normal = NewOccurrence(OccurrenceLocationMode.Point, 25m, "Assoreamento");
        var interrupted = NewOccurrence(OccurrenceLocationMode.Point, 25m, "Assoreamento");
        interrupted.InterruptedRecording = true;

        Assert.DoesNotContain("interrompendo a gravação", OccurrenceRules.GenerateDescription(normal));
        Assert.EndsWith(", interrompendo a gravação.", OccurrenceRules.GenerateDescription(interrupted));
    }

    [Fact]
    public async Task InterruptedRecordingPersistsWithoutChangingOlderDefaults()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 25m, "Obstrução");
        occurrence.InterruptedRecording = true;
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);

        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.True(saved.InterruptedRecording);
        Assert.Contains("interrompendo a gravação", saved.GeneratedDescription);
        Assert.False(new Occurrence().InterruptedRecording);
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

    [Fact]
    public async Task TechnicalAttributesPersistIndependentlyPerAnomaly()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18.35m, "Trinca", "Infiltração");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[0].Orientation = AnomalyOrientation.Longitudinal;
        occurrence.Anomalies[0].ApproximateExtension = 1.20m;
        occurrence.Anomalies[0].Dimension = "aprox. 5 mm";
        occurrence.Anomalies[0].Condition = AnomalyCondition.Moderate;
        occurrence.Anomalies[0].Notes = "Trinca longitudinal.";
        occurrence.Anomalies[1].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].InfiltrationCondition = InfiltrationCondition.ContinuousFlow;
        occurrence.ReferencePoint = "Próximo à contribuição lateral";
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);

        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        var crack = saved.Anomalies.Single(x => x.AnomalyType.Name == "Trinca");
        var infiltration = saved.Anomalies.Single(x => x.AnomalyType.Name == "Infiltração");
        Assert.Equal(AnomalyOrientation.Longitudinal, crack.Orientation);
        Assert.Equal(1.20m, crack.ApproximateExtension);
        Assert.Equal("aprox. 5 mm", crack.Dimension);
        Assert.Equal(AnomalyCondition.Moderate, crack.Condition);
        Assert.Equal("Trinca longitudinal.", crack.Notes);
        Assert.Equal(InfiltrationCondition.ContinuousFlow, infiltration.InfiltrationCondition);
        Assert.Contains("contribuição lateral", saved.GeneratedDescription);
        Assert.Contains("extensão aproximada de 1,2 m", saved.GeneratedDescription);
    }

    [Fact]
    public void EstimatedPercentageAcceptsBoundsAndRejectsValuesAboveOneHundred()
    {
        foreach (var valid in new[] { 0m, 25.5m, 100m })
        {
            var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 10m, "Obstrução");
            occurrence.Anomalies[0].EstimatedPercentage = valid;
            OccurrenceRules.Validate(occurrence);
        }
        var invalid = NewOccurrence(OccurrenceLocationMode.Point, 10m, "Obstrução");
        invalid.Anomalies[0].EstimatedPercentage = 100.01m;
        Assert.Throws<InvalidOperationException>(() => OccurrenceRules.Validate(invalid));
    }

    [Fact]
    public async Task PrimaryEvidenceAndEvidenceOrderPersist()
    {
        var (project, section) = await CreateProjectAsync();
        var first = AddCapture(project, section, 1000);
        var second = AddCapture(project, section, 2000);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Fissura");
        occurrence.Evidence.Add(OccurrenceService.FromCapture(first));
        var main = OccurrenceService.FromCapture(second); main.IsPrimary = true;
        occurrence.Evidence.Add(main);
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal(second.Id, saved.Evidence.Single(x => x.IsPrimary).CaptureId);
        Assert.Equal([first.Id, second.Id], saved.Evidence.OrderBy(x => x.Order).Select(x => x.CaptureId!.Value).ToArray());
    }

    [Fact]
    public void DuplicateCopiesTechnicalConfigurationButClearsLocationVideoAndEvidence()
    {
        var section = new InspectionSection { Id = _sectionId };
        var source = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        source.VideoReferenceId = Guid.NewGuid(); source.VideoTimestamp = TimeSpan.FromSeconds(12);
        source.Anomalies[0].Orientation = AnomalyOrientation.Diagonal;
        source.Anomalies[0].Dimension = "30 cm";
        source.Evidence.Add(new Evidence { OccurrenceId = source.Id, FilePath = "original.png" });
        var duplicate = _service.CreateDraft(section, source, true);
        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.Null(duplicate.Meterage);
        Assert.Null(duplicate.VideoReferenceId);
        Assert.Null(duplicate.VideoTimestamp);
        Assert.Empty(duplicate.Evidence);
        Assert.Equal(AnomalyOrientation.Diagonal, duplicate.Anomalies.Single().Orientation);
        Assert.Equal("30 cm", duplicate.Anomalies.Single().Dimension);
    }

    [Fact]
    public void ExpandedCatalogIsExtensibleAndFieldOriented()
    {
        var names = _catalog.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Fissura", names);
        Assert.Contains("Trinca", names);
        Assert.Contains("Raiz", names);
        Assert.Contains("Material depositado", names);
        Assert.Contains("Deslocamento de junta", names);
        Assert.Contains("Junta aberta", names);
        Assert.Contains("Rompimento", names);
        Assert.True(Definition("Trinca").SupportsOrientation);
        Assert.True(Definition("Infiltração").SupportsInfiltrationCondition);
        Assert.True(Definition("Obstrução").SupportsPercentage);
    }

    [Fact]
    public async Task MainTechnicalSmokePersistsOnePointTwoTypesAndThreeEvidence()
    {
        var (project, section) = await CreateProjectAsync();
        section.Name = "TRECHO 03"; section.StartPoint = "PV-04"; section.EndPoint = "PV-05";
        var capture = AddCapture(project, section, 278420);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18.35m, "Trinca", "Infiltração");
        occurrence.ReferencePoint = "Próximo à contribuição lateral";
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[0].Orientation = AnomalyOrientation.Longitudinal;
        occurrence.Anomalies[0].ApproximateExtension = 1.20m;
        occurrence.Anomalies[1].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].InfiltrationCondition = InfiltrationCondition.ContinuousFlow;
        occurrence.Evidence.Add(OccurrenceService.FromCapture(capture));
        foreach (var name in new[] { "evidencia-2.png", "evidencia-3.png" })
        {
            var path = Path.Combine(_root, name); await File.WriteAllBytesAsync(path, TestPng);
            occurrence.Evidence.Add(OccurrenceService.FromExternalImage(path));
        }
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal(2, saved.Anomalies.Count);
        Assert.Equal(3, saved.Evidence.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(278420), saved.Evidence.Single(x => x.CaptureId is not null).VideoTimestamp);
        Assert.Contains("Trinca e infiltração", saved.GeneratedDescription);
        Assert.True(saved.Evidence.Single(x => x.CaptureId is not null).VideoReferenceId is not null);
    }

    [Fact]
    public async Task QuickAdvancedFlowPersistsObstructionPercentageWithoutOtherDetails()
    {
        var (project, section) = await CreateProjectAsync();
        var capture = AddCapture(project, section, 30000);
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 30m, "Obstrução");
        occurrence.Anomalies[0].EstimatedPercentage = 40m;
        occurrence.Evidence.Add(OccurrenceService.FromCapture(capture));

        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);

        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal(40m, saved.Anomalies.Single().EstimatedPercentage);
        Assert.Single(saved.Evidence);
    }

    [Fact]
    public async Task ProductivityFlowPersistsTenConsecutiveMarkersInSameVideoContext()
    {
        var (project, section) = await CreateProjectAsync();
        for (var index = 0; index < 10; index++)
        {
            var capture = AddCapture(project, section, 1000 + index * 500);
            var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 10m + index, "Trinca");
            occurrence.Evidence.Add(OccurrenceService.FromCapture(capture));
            await _service.SaveAsync(project, section, occurrence, false);
        }
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!;
        var savedSection = reopened.Locations.Single().Sections.Single();
        Assert.Equal(10, savedSection.Occurrences.Count);
        Assert.Single(savedSection.VideoReferences);
        Assert.All(savedSection.Occurrences, occurrence => Assert.Single(occurrence.Evidence));
        Assert.Equal(10, savedSection.Occurrences.SelectMany(x => x.Evidence).Select(x => x.VideoTimestamp).Distinct().Count());
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
