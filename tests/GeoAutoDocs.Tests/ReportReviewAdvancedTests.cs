using GeoAutoDocs.Core;
using GeoAutoDocs.Core.Review;
using GeoAutoDocs.Core.Review.Rules;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class SectionSequenceRuleTests
{
    [Fact]
    public void MissingNumberProducesConservativeWarning()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho 01";
        AddSection(fixture, "Trecho 02", 1);
        var last = AddSection(fixture, "Trecho 04", 2);

        var issue = Assert.Single(fixture.Review(new SectionSequenceRule()).Issues);

        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
        Assert.Equal("Possível quebra na sequência", issue.Title);
        Assert.Contains("03", issue.Description);
        Assert.Equal(last.Id, issue.SectionId);
    }

    [Fact]
    public void ConsecutiveNumberingProducesNoIssue()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho 01";
        AddSection(fixture, "Trecho 02", 1);
        AddSection(fixture, "Trecho 03", 2);

        Assert.Empty(fixture.Review(new SectionSequenceRule()).Issues);
    }

    [Fact]
    public void IndependentPrefixesAreNotCompared()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho ES.1";
        AddSection(fixture, "Trecho EI.8", 1);

        Assert.Empty(fixture.Review(new SectionSequenceRule()).Issues);
    }

    [Fact]
    public void DecreasingNumberProducesWarning()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho 02";
        AddSection(fixture, "Trecho 01", 1);

        var issue = Assert.Single(fixture.Review(new SectionSequenceRule()).Issues);

        Assert.Equal("Numeração de trechos fora da ordem", issue.Title);
    }

    [Fact]
    public void LargeOrderedCollectionIsReviewedWithoutPairwiseComparison()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho 0001";
        for (var index = 2; index <= 2000; index++)
            AddSection(fixture, $"Trecho {index:D4}", index - 1);

        Assert.Empty(fixture.Review(new SectionSequenceRule()).Issues);
    }

    private static InspectionSection AddSection(
        ReviewFixture fixture,
        string name,
        int order)
    {
        var section = new InspectionSection
        {
            LocationId = fixture.Location.Id,
            Name = name,
            Order = order
        };
        fixture.Location.Sections.Add(section);
        fixture.Report.SelectSection(section);
        return section;
    }
}

public sealed class SectionContinuityRuleTests
{
    [Fact]
    public void EquivalentFormattedEndpointsAreContinuous()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.StartPoint = "PV 01";
        fixture.Section.EndPoint = "P.V. 02";
        var next = AddSection(fixture, "Trecho 02", 1);
        next.StartPoint = "PV 02";
        next.EndPoint = "PV 03";

        Assert.Empty(fixture.Review(new SectionContinuityRule()).Issues);
    }

    [Fact]
    public void DifferentAdjacentEndpointsProduceWarning()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.StartPoint = "PV 01";
        fixture.Section.EndPoint = "PV 04";
        var next = AddSection(fixture, "Trecho 02", 1);
        next.StartPoint = "PV 08";
        next.EndPoint = "PV 09";

        var issue = Assert.Single(fixture.Review(new SectionContinuityRule()).Issues);

        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
        Assert.Equal("Possível quebra de continuidade", issue.Title);
        Assert.Equal(next.Id, issue.SectionId);
        Assert.Contains("PV 04", issue.Description);
        Assert.Contains("PV 08", issue.Description);
    }

    [Fact]
    public void MissingOptionalEndpointsProduceNoIssue()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.EndPoint = null;
        AddSection(fixture, "Trecho 02", 1).StartPoint = "PV 02";

        Assert.Empty(fixture.Review(new SectionContinuityRule()).Issues);
    }

    [Fact]
    public void EqualOriginAndDestinationProduceWarning()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.StartPoint = "PV-01";
        fixture.Section.EndPoint = "PV 01";

        var issue = Assert.Single(fixture.Review(new SectionContinuityRule()).Issues);

        Assert.Equal("Origem e destino iguais", issue.Title);
        Assert.Equal(fixture.Section.Id, issue.SectionId);
    }

    private static InspectionSection AddSection(
        ReviewFixture fixture,
        string name,
        int order)
    {
        var section = new InspectionSection
        {
            LocationId = fixture.Location.Id,
            Name = name,
            Order = order
        };
        fixture.Location.Sections.Add(section);
        fixture.Report.SelectSection(section);
        return section;
    }
}

public sealed class NamingConsistencyRuleTests
{
    [Fact]
    public void PlainSectionSeriesSuggestsTwoDigitPadding()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho 01";
        var inconsistent = AddSection(fixture, "Trecho 2", 1);

        var issue = Assert.Single(fixture.Review(new NamingConsistencyRule()).Issues);

        Assert.Equal(ReviewSeverity.Info, issue.Severity);
        Assert.Equal("Formatação de nome inconsistente", issue.Title);
        Assert.Equal(inconsistent.Id, issue.SectionId);
        Assert.Contains("Trecho 02", issue.SuggestedFix!);
    }

    [Fact]
    public void ConsistentCustomSeriesIsPreserved()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Name = "Trecho ES.1";
        AddSection(fixture, "Trecho ES.2", 1);

        Assert.Empty(fixture.Review(new NamingConsistencyRule()).Issues);
    }

    [Fact]
    public void StoredAnomalyNameDifferentFromCurrentCatalogProducesInfo()
    {
        var fixture = ReviewFixture.Create();
        var occurrence = fixture.NewOccurrence(10m, "Raiz");
        occurrence.Anomalies[0].AnomalyName = "Raízes";
        fixture.Section.Occurrences.Add(occurrence);

        var issue = Assert.Single(fixture.Review(new NamingConsistencyRule()).Issues);

        Assert.Equal(ReviewSeverity.Info, issue.Severity);
        Assert.Equal("Nome de anomalia diferente do catálogo", issue.Title);
        Assert.Equal(occurrence.Id, issue.OccurrenceId);
        Assert.Contains("Raiz", issue.SuggestedFix!);
    }

    [Fact]
    public void MatchingCatalogNameProducesNoIssue()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10m, "Raiz"));

        Assert.Empty(fixture.Review(new NamingConsistencyRule()).Issues);
    }

    [Fact]
    public void AdvancedRulesCanBeDisabledWithoutAffectingOtherOptions()
    {
        var options = new ReviewOptions
        {
            CheckSectionSequence = false,
            CheckSectionContinuity = false,
            CheckNamingConsistency = false
        };
        var fixture = ReviewFixture.Create(options);
        fixture.Section.Name = "Trecho 02";
        fixture.Section.StartPoint = "PV 01";
        fixture.Section.EndPoint = "PV 01";
        var second = AddSection(fixture, "Trecho 04", 1);
        second.StartPoint = "PV 08";

        var result = new ReportReviewService(
        [
            new SectionSequenceRule(),
            new SectionContinuityRule(),
            new NamingConsistencyRule()
        ]).Review(fixture.Context());

        Assert.Empty(result.Issues);
    }

    private static InspectionSection AddSection(
        ReviewFixture fixture,
        string name,
        int order)
    {
        var section = new InspectionSection
        {
            LocationId = fixture.Location.Id,
            Name = name,
            Order = order
        };
        fixture.Location.Sections.Add(section);
        fixture.Report.SelectSection(section);
        return section;
    }
}
