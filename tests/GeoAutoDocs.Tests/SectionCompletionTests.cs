using GeoAutoDocs.Core;
using GeoAutoDocs.Core.Review;
using GeoAutoDocs.Core.Review.Rules;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class SectionCompletionTests
{
    [Fact]
    public void TerminalOccurrenceWithoutInterruptionAndWithDifferentMeterageProducesTwoErrors()
    {
        var fixture = ReviewFixture.Create();
        var first = Occurrence(5, order: 0, interrupted: false);
        var terminal = Occurrence(12.5m, order: 1, interrupted: false);
        fixture.Section.InvestigatedMeters = 10;
        fixture.Section.Occurrences.AddRange([first, terminal]);

        var result = fixture.Review(new SectionCompletionRule());

        Assert.Equal(2, result.ErrorCount);
        Assert.Contains(result.Issues, x => x.Title == "Última anomalia sem interrupção da gravação");
        Assert.Contains(result.Issues, x => x.Title == "Metragem inspecionada divergente");
        Assert.All(result.Issues, x =>
        {
            Assert.Equal(fixture.Location.Id, x.LocationId);
            Assert.Equal(fixture.Section.Id, x.SectionId);
            Assert.Equal(terminal.Id, x.OccurrenceId);
            Assert.Equal(12.5m, x.Meterage);
            Assert.Equal(ReviewNavigationArea.Inspection, x.NavigationArea);
        });
    }

    [Fact]
    public void MarkedTerminalOccurrenceWithMatchingMeterageIsValid()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.InvestigatedMeters = 18.75m;
        fixture.Section.Occurrences.Add(Occurrence(18.75m, 0, interrupted: true));

        Assert.Empty(fixture.Review(new SectionCompletionRule()).Issues);
    }

    [Fact]
    public void SectionWithoutOccurrencesIsNotChangedOrReported()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.InvestigatedMeters = 30;

        Assert.False(OccurrenceRules.SynchronizeInvestigatedMeters(fixture.Section));
        Assert.Equal(30m, fixture.Section.InvestigatedMeters);
        Assert.Empty(fixture.Review(new SectionCompletionRule()).Issues);
    }

    [Fact]
    public void OperationalOrderDefinesTerminalOccurrenceInsteadOfHighestMeterage()
    {
        var section = new InspectionSection { InvestigatedMeters = 40 };
        var highest = Occurrence(90, order: 0, interrupted: false);
        var terminal = Occurrence(40, order: 1, interrupted: true);
        section.Occurrences.AddRange([highest, terminal]);

        Assert.Same(terminal, OccurrenceRules.GetTerminalOccurrence(section));
        Assert.False(OccurrenceRules.SynchronizeInvestigatedMeters(section));
        Assert.Equal(40m, section.InvestigatedMeters);
    }

    [Theory]
    [InlineData(OccurrenceLocationMode.Point, 8.5)]
    [InlineData(OccurrenceLocationMode.From, 9.5)]
    [InlineData(OccurrenceLocationMode.Range, 15.5)]
    [InlineData(OccurrenceLocationMode.MultiPoint, 22.5)]
    public void FinalMeterageUsesTheEndOfEachLocationMode(
        OccurrenceLocationMode mode, double expected)
    {
        var occurrence = Occurrence(8.5m, 0, true);
        occurrence.LocationMode = mode;
        occurrence.Meterage = mode == OccurrenceLocationMode.From ? 9.5m : 8.5m;
        occurrence.EndMeterage = mode == OccurrenceLocationMode.Range ? 15.5m : null;
        occurrence.MeteragePoints = mode == OccurrenceLocationMode.MultiPoint
            ? [Point(3), Point(22.5m), Point(11)] : [];

        Assert.Equal((decimal)expected, OccurrenceRules.GetFinalMeterage(occurrence));
    }

    [Fact]
    public void TerminalOccurrenceWithoutFinalMeterageProducesErrorAndDoesNotInventValue()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.InvestigatedMeters = 14;
        fixture.Section.Occurrences.Add(new Occurrence
        {
            InspectionSectionId = fixture.Section.Id,
            LocationMode = OccurrenceLocationMode.EntireSection,
            InterruptedRecording = true
        });

        Assert.False(OccurrenceRules.SynchronizeInvestigatedMeters(fixture.Section));
        Assert.Equal(14m, fixture.Section.InvestigatedMeters);
        Assert.Contains(fixture.Review(new SectionCompletionRule()).Issues,
            x => x.Title == "Última anomalia sem metragem final");
    }

    [Fact]
    public void SynchronizationUpdatesSectionToTerminalMeterage()
    {
        var section = new InspectionSection { InvestigatedMeters = 2 };
        section.Occurrences.Add(Occurrence(17.25m, 0, true));

        Assert.True(OccurrenceRules.SynchronizeInvestigatedMeters(section));
        Assert.Equal(17.25m, section.InvestigatedMeters);
        Assert.False(OccurrenceRules.SynchronizeInvestigatedMeters(section));
    }

    private static Occurrence Occurrence(decimal meterage, int order, bool interrupted) => new()
    {
        InspectionSectionId = Guid.NewGuid(),
        LocationMode = OccurrenceLocationMode.Point,
        Meterage = meterage,
        Order = order,
        CreatedAt = DateTime.UtcNow.AddMinutes(order),
        InterruptedRecording = interrupted
    };

    private static OccurrenceMeteragePoint Point(decimal meterage) => new()
    {
        OccurrenceId = Guid.NewGuid(),
        Meterage = meterage
    };
}

public sealed class SectionMeteragePersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GeoAutoDocsSectionMeterageTests", Guid.NewGuid().ToString("N"));
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
    public async Task AggregateSavePersistsTerminalMeterageAsInvestigatedMeterage()
    {
        var project = await _store.CreateAsync("Sincronização");
        var location = new Location { ProjectId = project.Id, Name = "PV 01" };
        var section = new InspectionSection
        {
            LocationId = location.Id,
            Name = "Trecho 01",
            InvestigatedMeters = 999
        };
        section.Occurrences.Add(new Occurrence
        {
            InspectionSectionId = section.Id,
            LocationMode = OccurrenceLocationMode.Range,
            Meterage = 4,
            EndMeterage = 27.4m,
            InterruptedRecording = true,
            CreatedAt = DateTime.UtcNow
        });
        location.Sections.Add(section);
        project.Locations.Add(location);

        await _store.SaveAsync(project);
        var loaded = await _store.GetAsync(project.Id);
        var stored = Assert.Single(Assert.Single(loaded!.Locations).Sections);

        Assert.Equal(27.4m, stored.InvestigatedMeters);

        stored.InvestigatedMeters = 500;
        await _store.SaveSectionAsync(stored);
        loaded = await _store.GetAsync(project.Id);
        stored = Assert.Single(Assert.Single(loaded!.Locations).Sections);
        Assert.Equal(27.4m, stored.InvestigatedMeters);
    }
}
