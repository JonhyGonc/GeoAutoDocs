using System.Globalization;
using GeoAutoDocs.Core;
using GeoAutoDocs.Core.Review;
using GeoAutoDocs.Core.Review.Rules;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class ReportReviewConsolidationTests
{
    [Fact]
    public void CleanReportAndEmptyOptionalSectionFieldsHaveNoIssues()
    {
        var fixture = CleanFixture();
        var result = Review(fixture);

        Assert.True(result.IsComplete);
        Assert.Empty(result.Issues);
        Assert.Equal(new ReviewMetrics(1, 0, 0, 0), result.Metrics);
    }

    [Fact]
    public void SameDataProducesIdenticalIssuesAndOrderAcrossCultures()
    {
        var fixture = CleanFixture();
        fixture.Section.Name = "FILE";
        AddSection(fixture, "file", 0);
        fixture.Section.InvestigatedMeters = 10m;
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.01m, "Raiz"));
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.05m, "Raiz"));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var first = Review(fixture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = Review(fixture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var third = Review(fixture);

            Assert.Equal(first.Issues, second.Issues);
            Assert.Equal(first.Issues, third.Issues);
            Assert.Equal(first.Issues, Review(fixture).Issues);
            Assert.Single(first.Issues, x => x.Title == "Nome de trecho duplicado");
            Assert.Contains(first.Issues, x => x.Description.Contains("10,01", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void InterleavedIndependentSeriesDoNotProduceSequenceWarnings()
    {
        var fixture = CleanFixture();
        fixture.Section.Name = "Trecho ES.1";
        AddSection(fixture, "Trecho EI.1", 1);
        AddSection(fixture, "Trecho ES.2", 2);
        AddSection(fixture, "Trecho EI.2", 3);

        Assert.Empty(fixture.Review(new SectionSequenceRule()).Issues);
    }

    [Fact]
    public void MixedPaddingProducesInformationButNoFalseSequenceGap()
    {
        var fixture = CleanFixture();
        fixture.Section.Name = "Trecho 1";
        AddSection(fixture, "Trecho 02", 1);
        AddSection(fixture, "Trecho 3", 2);

        var result = Review(fixture);

        Assert.DoesNotContain(result.Issues, x => x.RuleId == "section.sequence");
        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, x => Assert.Equal(ReviewSeverity.Info, x.Severity));
    }

    [Fact]
    public void LegitimateNonnumericNamesAreNotEvaluatedAsSequences()
    {
        var fixture = CleanFixture();
        fixture.Section.Name = "Rede principal";
        AddSection(fixture, "Travessia", 1);
        AddSection(fixture, "Ramal A", 2);
        AddSection(fixture, "Interligação", 3);
        AddSection(fixture, "Trecho Norte", 4);

        Assert.Empty(Review(fixture).Issues);
    }

    [Theory]
    [InlineData(" P.v.  02 ", "pv-02", false)]
    [InlineData("PV 02", "PV 03", true)]
    [InlineData("A/B", "AB", true)]
    public void EndpointNormalizationIsConservative(string end, string start, bool expectedWarning)
    {
        var fixture = CleanFixture();
        fixture.Section.EndPoint = end;
        AddSection(fixture, "Trecho 02", 1).StartPoint = start;

        var issues = fixture.Review(new SectionContinuityRule()).Issues;

        Assert.Equal(expectedWarning, issues.Count > 0);
        Assert.All(issues, x => Assert.Equal(ReviewSeverity.Warning, x.Severity));
    }

    [Fact]
    public void IdenticalNamesWithinOneLocationAreReportedOnce()
    {
        var fixture = CleanFixture();
        AddSection(fixture, "Trecho 01", 1);
        var result = Review(fixture);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("Nome de trecho duplicado", issue.Title);
        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
        Assert.Equal(fixture.Location.Id, issue.LocationId);
    }

    [Fact]
    public void SameNumberWithDifferentSpellingStillProducesNumericWarning()
    {
        var fixture = CleanFixture();
        AddSection(fixture, "Trecho 1", 1);

        var issue = Assert.Single(fixture.Review(new SectionSequenceRule()).Issues);

        Assert.Equal("Numeração de trecho duplicada", issue.Title);
    }

    [Fact]
    public void IdenticalNamesAcrossLocationsRetainNumericWarning()
    {
        var fixture = CleanFixture();
        var location = new Location { ProjectId = fixture.Project.Id, Name = "PV 02", Order = 1 };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho 01" };
        location.Sections.Add(section);
        fixture.Project.Locations.Add(location);
        fixture.Report.SelectSection(section);

        Assert.Single(Review(fixture).Issues, x => x.Title == "Numeração de trecho duplicada");
    }

    [Theory]
    [InlineData("-")]
    [InlineData("teste")]
    [InlineData("DESCRIÇÃO")]
    [InlineData("digite aqui...")]
    public void PlaceholdersProduceOneActionableWarning(string text)
    {
        var fixture = CleanFixture();
        var occurrence = fixture.NewOccurrence(5m, "Raiz");
        occurrence.GeneratedDescription = text;
        fixture.Section.Occurrences.Add(occurrence);

        var issue = Assert.Single(fixture.Review(new TextQualityRule()).Issues);

        Assert.Equal("Texto provisório", issue.Title);
        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void ProblematicReportHasCorrectSeveritiesContextAndUniqueActions()
    {
        var fixture = CleanFixture();
        fixture.Section.Diameter = -100;
        fixture.Section.InvestigatedMeters = 20m;
        fixture.Section.EndPoint = "PV 02";
        var last = AddSection(fixture, "Trecho 03", 1);
        last.StartPoint = "PV 08";
        var occurrence = fixture.NewOccurrence(20.01m, "Raiz");
        occurrence.GeneratedDescription = "teste";
        occurrence.Anomalies[0].AnomalyName = "Nome histórico";
        var evidence = new Evidence { OccurrenceId = occurrence.Id, FilePath = "missing.png" };
        occurrence.Evidence.Add(evidence);
        fixture.Section.Occurrences.Add(occurrence);
        var capture = new Capture { ProjectId = fixture.Project.Id, SectionId = last.Id, FilePath = "pending.png" };
        fixture.Project.Captures.Add(capture);
        var context = new ReportReviewContext(fixture.Project, fixture.Report, "template.docx",
            new SelectiveFileSystem());

        var result = new ReportReviewService(ReportReviewRules.CreateDefault()).Review(context);

        Assert.True(result.IsComplete);
        Assert.Equal(7, result.Issues.Count);
        Assert.Equal(3, result.ErrorCount);
        Assert.Equal(3, result.WarningCount);
        Assert.Equal(1, result.InfoCount);
        Assert.Equal(result.Issues.Count, result.Issues
            .Select(x => (x.RuleId, x.Title, x.SectionId, x.OccurrenceId, x.EvidenceId)).Distinct().Count());
        var outside = Assert.Single(result.Issues, x => x.Title == "Ocorrência fora dos limites do trecho");
        Assert.Equal(occurrence.Id, outside.OccurrenceId);
        Assert.Equal(fixture.Location.Id, outside.LocationId);
        Assert.Equal(fixture.Section.Id, outside.SectionId);
        var missing = Assert.Single(result.Issues, x => x.ReportField == "Evidence");
        Assert.Equal(evidence.Id, missing.EvidenceId);
        var pending = Assert.Single(result.Issues, x => x.NavigationArea == ReviewNavigationArea.PendingCaptures);
        Assert.Equal(capture.Id, pending.CaptureId);
        Assert.Equal(last.Id, pending.SectionId);
        Assert.Equal(result.Issues.OrderBy(x => x.Severity), result.Issues);
    }

    [Fact]
    public void CancellationBeforeRulesDoesNotRunFileChecks()
    {
        var fixture = CleanFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var files = new CountingFileSystem();
        var context = new ReportReviewContext(fixture.Project, fixture.Report, "template.docx", files);

        Assert.Throws<OperationCanceledException>(() =>
            new ReportReviewService(ReportReviewRules.CreateDefault()).Review(context, cancellation.Token));
        Assert.Equal(0, files.Calls);
    }

    [Fact]
    public void CancellationAtEndOfLastRuleDoesNotReturnAnApprovedResult()
    {
        var fixture = CleanFixture();
        using var cancellation = new CancellationTokenSource();
        var service = new ReportReviewService([new CancellingRule(cancellation)]);

        Assert.Throws<OperationCanceledException>(() => service.Review(fixture.Context(), cancellation.Token));
    }

    [Fact]
    public void CancellationDuringFileCheckDoesNotReturnAnApprovedResult()
    {
        var fixture = CleanFixture();
        using var cancellation = new CancellationTokenSource();
        var files = new CountingFileSystem(cancellation);
        var context = new ReportReviewContext(fixture.Project, fixture.Report, "template.docx", files);

        Assert.Throws<OperationCanceledException>(() =>
            new ReportReviewService([new FileReferenceRule()]).Review(context, cancellation.Token));
        Assert.Equal(1, files.Calls);
    }

    [Fact]
    public void ProjectSwitchInvalidatesSnapshotButOperationalOutputPathDoesNot()
    {
        var fixture = CleanFixture();
        var snapshot = new ReportReviewSnapshot(fixture.Project.Id, fixture.Report.Id, 10,
            DateTimeOffset.UtcNow, Review(fixture))
        {
            SectionSelectionSignature = ReportReviewSelection.CreateSignature(fixture.Report)
        };
        fixture.Report.GeneratedReportPath = "generated.docx";

        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration,
            ReportGenerationReviewPolicy.Evaluate(snapshot, fixture.Project.Id, fixture.Report.Id, 10,
                ReportReviewSelection.CreateSignature(fixture.Report)));
        Assert.Equal(ReportGenerationReviewDecision.ExecuteReview,
            ReportGenerationReviewPolicy.Evaluate(snapshot, Guid.NewGuid(), fixture.Report.Id, 10,
                snapshot.SectionSelectionSignature));
    }

    [Fact]
    public void LargeLocationExercisesAllRulesIndexesAndLightweightFileChecks()
    {
        var fixture = CleanFixture();
        fixture.Section.Name = "Trecho 0001";
        for (var index = 0; index < 2000; index++)
        {
            var section = index == 0 ? fixture.Section :
                AddSection(fixture, $"Trecho {index + 1:D4}", index);
            section.StartPoint = $"PV {index:D4}";
            section.EndPoint = $"PV {index + 1:D4}";
            section.InvestigatedMeters = 20m;
            var video = new VideoReference { InspectionSectionId = section.Id, FilePath = $"video{index}.mp4" };
            section.VideoReferences.Add(video);
            var occurrence = fixture.NewOccurrence(5m, "Raiz");
            occurrence.InspectionSectionId = section.Id;
            occurrence.VideoReferenceId = video.Id;
            occurrence.Evidence.Add(new Evidence { OccurrenceId = occurrence.Id, FilePath = $"evidence{index}.png" });
            section.Occurrences.Add(occurrence);
            fixture.Project.Captures.Add(new Capture
            {
                ProjectId = fixture.Project.Id, SectionId = section.Id,
                VideoReferenceId = video.Id, FilePath = $"pending{index}.png"
            });
        }
        var files = new CountingFileSystem();
        var context = new ReportReviewContext(fixture.Project, fixture.Report, "template.docx", files);
        var result = new ReportReviewService(ReportReviewRules.CreateDefault()).Review(context);

        Assert.True(result.IsComplete);
        Assert.Equal(new ReviewMetrics(2000, 2000, 2000, 2000), result.Metrics);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(2000, result.WarningCount);
        Assert.Equal(0, result.InfoCount);
        Assert.Equal(4001, files.Calls);
        Assert.All(context.SelectedSections, x =>
        {
            Assert.Single(context.GetPendingCaptures(x.Section.Id));
            Assert.True(context.TryGetSection(x.Section.Id, out var selected));
            Assert.Same(x, selected);
            Assert.True(context.TryGetLocation(x.Location.Id, out var location));
            Assert.Same(fixture.Location, location);
        });
        Assert.Empty(context.GetPendingCaptures(Guid.NewGuid()));
    }

    private static ReviewFixture CleanFixture()
    {
        var fixture = ReviewFixture.Create();
        fixture.Report.Client = "Cliente";
        fixture.Report.Area = "Área";
        fixture.Report.ReportCode = "REV-01";
        fixture.Report.Contract = "Contrato";
        fixture.Report.PurchaseOrder = "Pedido";
        fixture.Report.ReportDate = new DateTime(2026, 9, 15);
        fixture.Report.TotalInvestigatedMeters = 20m;
        fixture.Report.CustomDiameters = "300 mm";
        return fixture;
    }

    private static ReviewResult Review(ReviewFixture fixture) =>
        new ReportReviewService(ReportReviewRules.CreateDefault()).Review(fixture.Context());

    private static InspectionSection AddSection(ReviewFixture fixture, string name, int order)
    {
        var section = new InspectionSection { LocationId = fixture.Location.Id, Name = name, Order = order };
        fixture.Location.Sections.Add(section);
        fixture.Report.SectionLinks.Add(new ReportSection
        {
            ReportId = fixture.Report.Id, SectionId = section.Id, Section = section, Order = order
        });
        return section;
    }

    private sealed class SelectiveFileSystem : IReviewFileSystem
    {
        public bool FileExists(string path) => path != "missing.png";
    }

    private sealed class CountingFileSystem(CancellationTokenSource? cancellation = null) : IReviewFileSystem
    {
        public int Calls { get; private set; }
        public bool FileExists(string path)
        {
            Calls++;
            cancellation?.Cancel();
            return true;
        }
    }

    private sealed class CancellingRule(CancellationTokenSource cancellation) : IReportReviewRule
    {
        public string Id => "test.cancel-at-end";
        public string Name => Id;
        public IEnumerable<ReviewIssue> Evaluate(ReportReviewContext context, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            yield break;
        }
    }
}
