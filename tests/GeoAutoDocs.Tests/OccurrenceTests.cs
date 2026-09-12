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

    [Theory]
    [InlineData("9", "9")]
    [InlineData("9,5", "9,5")]
    [InlineData("9,25", "9,25")]
    [InlineData("9,00", "9")]
    [InlineData("9.00", "9")]
    public void MeasurementRoundTripDoesNotChangeValue(string input, string expected)
    {
        Assert.True(MeasurementText.TryParse(input, out var value));
        Assert.Equal(expected, MeasurementText.Format(value));
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
    public async Task MultiPointWithTwoValuesIsSortedFormattedAndPersisted()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.MultiPoint, null, "Trinca");
        occurrence.MeteragePoints.AddRange([
            new OccurrenceMeteragePoint { OccurrenceId = occurrence.Id, Meterage = 13m },
            new OccurrenceMeteragePoint { OccurrenceId = occurrence.Id, Meterage = 12m }
        ]);

        var saved = await _service.SaveAsync(project, section, occurrence, false);
        Assert.Equal("Trinca a 12 e 13 metros do início da filmagem.", saved.GeneratedDescription);
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal([12m, 13m], reopened.MeteragePoints.OrderBy(x => x.Order).Select(x => x.Meterage).ToArray());
        Assert.Equal(12m, reopened.Meterage);
        Assert.Null(reopened.EndMeterage);
    }

    [Fact]
    public void MultiPointWithSeveralValuesUsesNaturalPortugueseList()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.MultiPoint, 2m, "Infiltração");
        occurrence.MeteragePoints.AddRange(new[] { 2m, 3m, 4m, 5m }.Select((value, index) =>
            new OccurrenceMeteragePoint { OccurrenceId = occurrence.Id, Meterage = value, Order = index }));

        OccurrenceRules.Validate(occurrence);

        Assert.Equal("Infiltração a 2, 3, 4 e 5 metros do início da filmagem.",
            OccurrenceRules.GenerateDescription(occurrence));
    }

    [Fact]
    public void InvalidMultiPointIsRejectedWithoutSilentlyDiscardingDuplicates()
    {
        var tooShort = NewOccurrence(OccurrenceLocationMode.MultiPoint, null, "Trinca");
        tooShort.MeteragePoints.Add(new OccurrenceMeteragePoint { OccurrenceId = tooShort.Id, Meterage = 2m });
        Assert.Throws<InvalidOperationException>(() => OccurrenceRules.Validate(tooShort));

        var duplicate = NewOccurrence(OccurrenceLocationMode.MultiPoint, 2m, "Trinca");
        duplicate.MeteragePoints.AddRange([
            new OccurrenceMeteragePoint { OccurrenceId = duplicate.Id, Meterage = 2m, Order = 0 },
            new OccurrenceMeteragePoint { OccurrenceId = duplicate.Id, Meterage = 2m, Order = 1 }
        ]);
        Assert.Throws<InvalidOperationException>(() => OccurrenceRules.Validate(duplicate));
    }

    [Fact]
    public async Task FromUsesOnlyInitialMeterageAndPersists()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.From, 50m, "Infiltração");
        occurrence.EndMeterage = 80m;

        var saved = await _service.SaveAsync(project, section, occurrence, false);
        Assert.Equal("Infiltração a partir de 50 metros do início da filmagem.", saved.GeneratedDescription);
        Assert.Null(saved.EndMeterage);
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal(OccurrenceLocationMode.From, reopened.LocationMode);
        Assert.Equal(50m, reopened.Meterage);
        Assert.Null(reopened.EndMeterage);
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
        occurrence.Anomalies[1].Position = AnomalyPosition.UpperAndLowerGenerators;
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);
        var saved = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0].Occurrences.Single();
        Assert.Equal([AnomalyPosition.RightSide, AnomalyPosition.UpperAndLowerGenerators],
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
    public void FormatterSupportsUpperAndLowerGenerators()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18m, "Trinca");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperAndLowerGenerators;

        Assert.Contains(AnomalyPosition.UpperAndLowerGenerators, OccurrenceRules.SelectablePositions);
        Assert.Equal("Geratriz superior e inferior",
            OccurrenceRules.FormatPosition(AnomalyPosition.UpperAndLowerGenerators));
        Assert.Equal("Trinca nas geratrizes superior e inferior a 18 metros do início da filmagem.",
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
    public void FormatterAddsSevereMarkerAfterTheDescription()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 25m, "Assoreamento");
        occurrence.IsSevere = true;

        Assert.EndsWith(" (grave).", OccurrenceRules.GenerateDescription(occurrence));
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
    public async Task SevereClassificationPersistsAndIsCopiedToTheEditDraft()
    {
        var (project, section) = await CreateProjectAsync();
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 25m, "Obstrução");
        occurrence.IsSevere = true;
        await _service.SaveAsync(project, section, occurrence, false);
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!.Locations[0].Sections[0];
        var saved = Assert.Single(reopened.Occurrences);
        Assert.True(saved.IsSevere);
        Assert.True(_service.CreateDraft(reopened, saved).IsSevere);
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
        Assert.Contains("Deslocamento das juntas", names);
        Assert.Contains("Junta aberta", names);
        Assert.Contains("Rompimento", names);
        Assert.True(Definition("Trinca").SupportsOrientation);
        Assert.True(Definition("Infiltração").SupportsInfiltrationCondition);
        Assert.True(Definition("Obstrução").SupportsPercentage);
    }

    [Fact]
    public void PrimaryCatalogAndPositionVocabularyMatchTheOperationalForm()
    {
        var primaryNames = AnomalyCatalog.Defaults.Where(x => AnomalyCatalog.PrimaryIds.Contains(x.Id))
            .Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(8, primaryNames.Count);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Fissura", "Trinca", "Infiltração", "Obstrução", "Contribuição",
            "Deslocamento das juntas", "Abaulamento", "Caixa de passagem"
        }, primaryNames);
        Assert.Equal([
            AnomalyPosition.NotApplicable, AnomalyPosition.UpperGenerator, AnomalyPosition.LowerGenerator,
            AnomalyPosition.UpperAndLowerGenerators,
            AnomalyPosition.LeftSide, AnomalyPosition.RightSide, AnomalyPosition.Circumferential
        ], OccurrenceRules.SelectablePositions);
    }

    [Fact]
    public void FigureCaptionContainsOnlyAnomalyAndPosition()
    {
        var occurrence = NewOccurrence(OccurrenceLocationMode.Point, 18.35m, "Trinca");
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        Assert.Equal("Trinca na geratriz superior.", OccurrenceRules.GenerateCaptionSummary(occurrence));
        Assert.DoesNotContain("18", OccurrenceRules.GenerateCaptionSummary(occurrence));
        Assert.DoesNotContain("filmagem", OccurrenceRules.GenerateCaptionSummary(occurrence));
    }

    [Fact]
    public void ReportCodeIsTheOnlyNewReportIdentity()
    {
        var report = new Report { Name = "Nome legado", ReportCode = "GF-RL-001" };
        Assert.Equal("GF-RL-001", report.DisplayCode);
        report.ReportCode = "";
        Assert.Equal("Nome legado", report.DisplayCode);
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

    [Fact]
    public async Task ManualOccurrenceOrderPersistsAfterReload()
    {
        var (project, section) = await CreateProjectAsync();
        var first = NewOccurrence(OccurrenceLocationMode.Point, 10m, "Trinca");
        var second = NewOccurrence(OccurrenceLocationMode.Point, 20m, "Infiltração");
        await _service.SaveAsync(project, section, first, false);
        await _service.SaveAsync(project, section, second, false);
        first.Order = 1;
        second.Order = 0;
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!;
        var ordered = reopened.Locations.Single().Sections.Single().Occurrences.OrderBy(x => x.Order).ToList();
        Assert.Equal([second.Id, first.Id], ordered.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task CompletedSectionPersistsWithoutDuplicatingOccurrences()
    {
        var (project, section) = await CreateProjectAsync();
        await _service.SaveAsync(project, section, NewOccurrence(OccurrenceLocationMode.Point, 9m, "Trinca"), false);
        section.AnomalyReviewStatus = SectionAnomalyReviewStatus.Completed;
        section.AnomalyReviewUpdatedAt = DateTime.UtcNow;
        await _store.SaveAsync(project);
        await _store.SaveAsync(project);

        var reopened = (await _store.GetAsync(project.Id))!.Locations.Single().Sections.Single();
        Assert.Equal(SectionAnomalyReviewStatus.Completed, reopened.AnomalyReviewStatus);
        Assert.NotNull(reopened.AnomalyReviewUpdatedAt);
        Assert.Single(reopened.Occurrences);
    }

    [Fact]
    public async Task SectionReviewReportsIncompleteOccurrencesAndPendingCaptures()
    {
        var (project, section) = await CreateProjectAsync();
        await _service.SaveAsync(project, section, NewOccurrence(OccurrenceLocationMode.Point, 9m, "Trinca"), false);
        AddCapture(project, section, 1000);

        var pending = SectionReviewRules.GetPending(project, section);
        Assert.True(pending.HasPending);
        Assert.Equal(1, pending.IncompleteOccurrences);
        Assert.Equal(1, pending.PendingCaptures);
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
