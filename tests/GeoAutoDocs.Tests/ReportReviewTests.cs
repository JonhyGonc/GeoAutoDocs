using GeoAutoDocs.Core;
using GeoAutoDocs.Core.Review;
using GeoAutoDocs.Core.Review.Rules;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class ReviewStructureRuleTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData(300.0, 0)]
    [InlineData(0.0, 1)]
    [InlineData(-300.0, 1)]
    public void OptionalDiameterOnlyFailsWhenProvidedValueIsInvalid(double? diameter, int expectedErrors)
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Diameter = diameter is null ? null : (decimal)diameter.Value;

        var result = fixture.Review(new StructureRule());

        Assert.Equal(expectedErrors,
            result.Issues.Count(x => x.Title == "Diâmetro inválido"));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(20.0, 0)]
    [InlineData(0.0, 1)]
    [InlineData(-1.0, 1)]
    public void OptionalLengthOnlyFailsWhenProvidedValueIsInvalid(double? length, int expectedErrors)
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.InvestigatedMeters = length is null ? null : (decimal)length.Value;

        var result = fixture.Review(new StructureRule());

        Assert.Equal(expectedErrors,
            result.Issues.Count(x => x.Title == "Metragem inválida"));
    }

    [Fact]
    public void DuplicateSectionNamesAreWarningsWithinTheSameLocation()
    {
        var fixture = ReviewFixture.Create();
        var duplicate = new InspectionSection
        {
            LocationId = fixture.Location.Id,
            Name = fixture.Section.Name,
            Order = 1
        };
        fixture.Location.Sections.Add(duplicate);
        fixture.Report.SelectSection(duplicate);

        var result = fixture.Review(new StructureRule());

        var issue = Assert.Single(result.Issues, x => x.Title == "Nome de trecho duplicado");
        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
        Assert.Equal(fixture.Location.Id, issue.LocationId);
    }
}

public sealed class ReviewOccurrenceRuleTests
{
    [Theory]
    [InlineData(15, false)]
    [InlineData(20, false)]
    [InlineData(20.01, true)]
    public void OccurrenceBoundaryUsesKnownSectionLength(double meterage, bool expectedError)
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.InvestigatedMeters = 20m;
        fixture.Section.Occurrences.Add(fixture.NewOccurrence((decimal)meterage, "Raiz"));

        var result = fixture.Review(new OccurrenceValidityRule());

        Assert.Equal(expectedError,
            result.Issues.Any(x => x.Title == "Ocorrência fora dos limites do trecho"));
    }

    [Fact]
    public void MissingRequiredCatalogPositionIsAnError()
    {
        var fixture = ReviewFixture.Create();
        var definition = new AnomalyDefinition
        {
            Name = "Anomalia posicionada",
            RequiresPosition = true,
            SupportsPosition = true
        };
        var occurrence = fixture.NewOccurrence(5m, definition);
        occurrence.Anomalies[0].Position = AnomalyPosition.NotReported;
        fixture.Section.Occurrences.Add(occurrence);

        var issue = Assert.Single(
            fixture.Review(new OccurrenceValidityRule()).Issues,
            x => x.Description.Contains("Informe a posição", StringComparison.Ordinal));

        Assert.Equal(ReviewSeverity.Error, issue.Severity);
        Assert.Equal(occurrence.Id, issue.OccurrenceId);
    }

    [Fact]
    public void MissingRequiredCustomTextReusesOccurrenceRules()
    {
        var fixture = ReviewFixture.Create();
        var definition = new AnomalyDefinition
        {
            Name = "Outro",
            AllowsCustomText = true
        };
        var occurrence = fixture.NewOccurrence(5m, definition);
        occurrence.Anomalies[0].CustomText = " ";
        fixture.Section.Occurrences.Add(occurrence);

        var issue = Assert.Single(
            fixture.Review(new OccurrenceValidityRule()).Issues,
            x => x.Description.Contains("Descreva a anomalia", StringComparison.Ordinal));

        Assert.Equal(nameof(OccurrenceAnomaly.CustomText), issue.ReportField);
    }
}

public sealed class DuplicateOccurrenceRuleTests
{
    [Fact]
    public void SameTypeWithinToleranceProducesWarning()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.00m, "Raiz"));
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.05m, "Raiz"));

        var issue = Assert.Single(fixture.Review(new DuplicateOccurrenceRule()).Issues);

        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
        Assert.Equal("Possíveis ocorrências duplicadas", issue.Title);
    }

    [Fact]
    public void DifferentTypesWithinToleranceAreNotDuplicates()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.00m, "Raiz"));
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.05m, "Trinca"));

        Assert.Empty(fixture.Review(new DuplicateOccurrenceRule()).Issues);
    }

    [Fact]
    public void SameTypeOutsideToleranceIsNotDuplicate()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.00m, "Raiz"));
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.50m, "Raiz"));

        Assert.Empty(fixture.Review(new DuplicateOccurrenceRule()).Issues);
    }

    [Fact]
    public void ConfiguredToleranceIsUsed()
    {
        var fixture = ReviewFixture.Create(new ReviewOptions
        {
            DuplicateOccurrenceToleranceMeters = 0.01m
        });
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.00m, "Raiz"));
        fixture.Section.Occurrences.Add(fixture.NewOccurrence(10.05m, "Raiz"));

        Assert.Empty(fixture.Review(new DuplicateOccurrenceRule()).Issues);
    }
}

public sealed class TextQualityRuleTests
{
    [Theory]
    [InlineData(null, "Descrição vazia")]
    [InlineData("", "Descrição vazia")]
    [InlineData("   ", "Descrição vazia")]
    [InlineData("teste", "Texto provisório")]
    [InlineData("xxx", "Texto provisório")]
    [InlineData("digite aqui", "Texto provisório")]
    [InlineData("Presença de raiz raiz no interior da tubulação.", "Palavra repetida")]
    [InlineData("Presença  de raiz no interior da tubulação.", "Espaços consecutivos")]
    public void ObjectiveTextProblemsAreDetected(string? text, string expectedTitle)
    {
        var fixture = ReviewFixture.Create();
        var occurrence = fixture.NewOccurrence(5m, "Raiz");
        occurrence.GeneratedDescription = text ?? string.Empty;
        fixture.Section.Occurrences.Add(occurrence);

        Assert.Contains(fixture.Review(new TextQualityRule()).Issues,
            x => x.Title == expectedTitle);
    }

    [Fact]
    public void NormalTextHasNoQualityIssue()
    {
        var fixture = ReviewFixture.Create();
        var occurrence = fixture.NewOccurrence(5m, "Raiz");
        occurrence.GeneratedDescription = "Presença de raiz na tubulação.";
        fixture.Section.Occurrences.Add(occurrence);

        Assert.Empty(fixture.Review(new TextQualityRule()).Issues);
    }
}

public sealed class ReportReviewServiceTests
{
    [Fact]
    public void BrokenRuleDoesNotDiscardOtherResultsAndIsRecorded()
    {
        var fixture = ReviewFixture.Create();
        var logger = new RecordingLogger();
        var service = new ReportReviewService(
            [new HealthyRule(), new BrokenRule(), new SecondHealthyRule()],
            logger);

        var result = service.Review(fixture.Context());

        Assert.Equal(2, result.Issues.Count);
        Assert.False(result.IsComplete);
        Assert.True(result.HasRuleFailures);
        var failure = Assert.Single(result.RuleFailures);
        Assert.Equal("test.broken", failure.RuleId);
        Assert.Contains("falha deliberada", failure.Message);
        Assert.Equal("test.broken", Assert.Single(logger.RuleIds));
    }

    [Fact]
    public void CountsAndBlockingStateDoNotRequireStringParsing()
    {
        var fixture = ReviewFixture.Create();
        var result = new ReportReviewService(
            [new CountingRule()]).Review(fixture.Context());

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
        Assert.Equal(1, result.InfoCount);
        Assert.True(result.HasErrors);
        Assert.True(result.IsComplete);
    }

    private sealed class HealthyRule : TestRule
    {
        public HealthyRule() : base("test.healthy", ReviewSeverity.Warning) { }
    }

    private sealed class SecondHealthyRule : TestRule
    {
        public SecondHealthyRule() : base("test.second", ReviewSeverity.Info) { }
    }

    private sealed class CountingRule : IReportReviewRule
    {
        public string Id => "test.count";
        public string Name => "Contagem";

        public IEnumerable<ReviewIssue> Evaluate(
            ReportReviewContext context,
            CancellationToken cancellationToken = default)
        {
            yield return Issue(ReviewSeverity.Error);
            yield return Issue(ReviewSeverity.Warning);
            yield return Issue(ReviewSeverity.Info);
        }

        private ReviewIssue Issue(ReviewSeverity severity) => new()
        {
            RuleId = Id,
            Severity = severity,
            Title = severity.ToString(),
            Description = severity.ToString()
        };
    }

    private class TestRule : IReportReviewRule
    {
        private readonly ReviewSeverity _severity;

        protected TestRule(string id, ReviewSeverity severity)
        {
            Id = id;
            _severity = severity;
        }

        public string Id { get; }
        public string Name => Id;

        public IEnumerable<ReviewIssue> Evaluate(
            ReportReviewContext context,
            CancellationToken cancellationToken = default)
        {
            yield return new ReviewIssue
            {
                RuleId = Id,
                Severity = _severity,
                Title = Id,
                Description = Id
            };
        }
    }

    private sealed class BrokenRule : IReportReviewRule
    {
        public string Id => "test.broken";
        public string Name => "Regra quebrada";

        public IEnumerable<ReviewIssue> Evaluate(
            ReportReviewContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("falha deliberada");
    }

    private sealed class RecordingLogger : IReportReviewLogger
    {
        public List<string> RuleIds { get; } = [];

        public void RuleFailed(string ruleId, string ruleName, Exception exception) =>
            RuleIds.Add(ruleId);
    }
}

internal sealed class ReviewFixture
{
    private readonly ReviewOptions _options;

    private ReviewFixture(ReviewOptions options)
    {
        _options = options;
        Project = new Project { Name = "Projeto de teste" };
        Location = new Location
        {
            ProjectId = Project.Id,
            Name = "PV 01",
            Order = 0
        };
        Section = new InspectionSection
        {
            LocationId = Location.Id,
            Name = "Trecho 01",
            Order = 0
        };
        Location.Sections.Add(Section);
        Project.Locations.Add(Location);
        Report = Project.CreateReport("Relatório");
        Report.SelectSection(Section);
    }

    public Project Project { get; }
    public Report Report { get; }
    public Location Location { get; }
    public InspectionSection Section { get; }

    public static ReviewFixture Create(ReviewOptions? options = null) =>
        new(options ?? ReviewOptions.Default);

    public ReportReviewContext Context() =>
        new(Project, Report, @"C:\template.docx", AllFilesExist.Instance, _options);

    public ReviewResult Review(IReportReviewRule rule) =>
        new ReportReviewService([rule]).Review(Context());

    public Occurrence NewOccurrence(decimal meterage, string anomalyName)
    {
        var definition = AnomalyCatalog.Defaults.Single(x => x.Name == anomalyName);
        return NewOccurrence(meterage, definition);
    }

    public Occurrence NewOccurrence(decimal meterage, AnomalyDefinition definition)
    {
        var occurrence = new Occurrence
        {
            InspectionSectionId = Section.Id,
            LocationMode = OccurrenceLocationMode.Point,
            Meterage = meterage,
            GeneratedDescription = $"Presença de {definition.Name.ToLowerInvariant()} na tubulação."
        };
        occurrence.Anomalies.Add(new OccurrenceAnomaly
        {
            OccurrenceId = occurrence.Id,
            AnomalyTypeId = definition.Id,
            AnomalyType = definition,
            AnomalyName = definition.Name
        });
        return occurrence;
    }

    private sealed class AllFilesExist : IReviewFileSystem
    {
        public static AllFilesExist Instance { get; } = new();
        public bool FileExists(string path) => true;
    }
}


public sealed class ReportReviewPresentationTests
{
    private static readonly ReviewIssue[] Issues =
    [
        Issue(ReviewSeverity.Error, "erro"),
        Issue(ReviewSeverity.Warning, "aviso"),
        Issue(ReviewSeverity.Info, "informação")
    ];

    [Theory]
    [InlineData(ReviewIssueFilter.All, 3)]
    [InlineData(ReviewIssueFilter.Errors, 1)]
    [InlineData(ReviewIssueFilter.Warnings, 1)]
    [InlineData(ReviewIssueFilter.Information, 1)]
    public void FilterUsesExistingSnapshotWithoutReordering(
        ReviewIssueFilter filter, int expectedCount)
    {
        var filtered = ReviewIssueFiltering.Apply(Issues, filter);

        Assert.Equal(expectedCount, filtered.Count);
        Assert.True(filtered.SequenceEqual(Issues.Where(issue => filter switch
        {
            ReviewIssueFilter.Errors => issue.Severity == ReviewSeverity.Error,
            ReviewIssueFilter.Warnings => issue.Severity == ReviewSeverity.Warning,
            ReviewIssueFilter.Information => issue.Severity == ReviewSeverity.Info,
            _ => true
        })));
    }

    [Fact]
    public void SnapshotBecomesStaleOnlyAfterDataRevisionChanges()
    {
        var projectId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var snapshot = new ReportReviewSnapshot(
            projectId,
            reportId,
            14,
            DateTimeOffset.UtcNow,
            new ReviewResult([], [], new ReviewMetrics(0, 0, 0, 0)));

        Assert.True(snapshot.AppliesTo(projectId, reportId));
        Assert.False(snapshot.IsStale(projectId, reportId, 14));
        Assert.True(snapshot.IsStale(projectId, reportId, 15));
        Assert.False(snapshot.IsStale(projectId, Guid.NewGuid(), 15));
    }

    [Fact]
    public void NavigationTargetCopiesStructuredIssueContext()
    {
        var locationId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        var captureId = Guid.NewGuid();
        var issue = new ReviewIssue
        {
            RuleId = "test.navigation",
            Severity = ReviewSeverity.Warning,
            Title = "Captura pendente",
            Description = "Revise a captura.",
            NavigationArea = ReviewNavigationArea.PendingCaptures,
            LocationId = locationId,
            SectionId = sectionId,
            OccurrenceId = occurrenceId,
            CaptureId = captureId,
            ReportField = "Evidence"
        };

        var target = ReviewNavigationTarget.FromIssue(issue);

        Assert.Equal(ReviewNavigationArea.PendingCaptures, target.Area);
        Assert.Equal(locationId, target.LocationId);
        Assert.Equal(sectionId, target.SectionId);
        Assert.Equal(occurrenceId, target.OccurrenceId);
        Assert.Equal(captureId, target.CaptureId);
        Assert.Equal("Evidence", target.ReportField);
    }

    private static ReviewIssue Issue(ReviewSeverity severity, string title) => new()
    {
        RuleId = "test." + title,
        Severity = severity,
        Title = title,
        Description = title
    };
}


public sealed class ReportGenerationReviewPolicyTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _reportId = Guid.NewGuid();
    private const string Selection = "01;02;03";

    [Fact]
    public void CompleteReviewWithoutIssuesAllowsGeneration() =>
        Assert.Equal(
            ReportGenerationReviewDecision.AllowGeneration,
            Evaluate(Snapshot()));

    [Fact]
    public void InformationDoesNotRequireConfirmation() =>
        Assert.Equal(
            ReportGenerationReviewDecision.AllowGeneration,
            Evaluate(Snapshot(issues: [Issue(ReviewSeverity.Info)])));

    [Fact]
    public void ErrorsBlockGeneration() =>
        Assert.Equal(
            ReportGenerationReviewDecision.BlockErrors,
            Evaluate(Snapshot(issues: [Issue(ReviewSeverity.Error)])));

    [Fact]
    public void WarningsRequireUserDecision() =>
        Assert.Equal(
            ReportGenerationReviewDecision.ConfirmWarnings,
            Evaluate(Snapshot(issues: [Issue(ReviewSeverity.Warning)])));

    [Fact]
    public void IncompleteReviewBlocksGeneration() =>
        Assert.Equal(
            ReportGenerationReviewDecision.BlockIncomplete,
            Evaluate(Snapshot(incomplete: true)));

    [Fact]
    public void MissingReviewRequestsExecution() =>
        Assert.Equal(
            ReportGenerationReviewDecision.ExecuteReview,
            Evaluate(null));

    [Fact]
    public void StaleReviewRequestsExecution() =>
        Assert.Equal(
            ReportGenerationReviewDecision.ExecuteReview,
            Evaluate(Snapshot(revision: 9)));

    [Fact]
    public void CurrentReviewIsReused() =>
        Assert.Equal(
            ReportGenerationReviewDecision.AllowGeneration,
            Evaluate(Snapshot(revision: 10)));

    [Fact]
    public void CancelledReviewCancelsGeneration()
    {
        var outcome = ReportReviewExecutionOutcome.Cancelled();

        Assert.Equal(ReportReviewExecutionStatus.Cancelled, outcome.Status);
        Assert.Null(outcome.Snapshot);
    }

    [Fact]
    public void AcceptedWarningsContinueWithoutAnotherReview()
    {
        var snapshot = Snapshot(issues: [Issue(ReviewSeverity.Warning)]);

        var decision = ReportGenerationReviewPolicy.Evaluate(
            snapshot, _projectId, _reportId, 10, Selection, warningsAccepted: true);

        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, decision);
    }

    [Fact]
    public void NewSnapshotRestoresCurrentStateAfterRevisionChanges()
    {
        var revisionTen = Snapshot(revision: 10);

        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, Evaluate(revisionTen, 10));
        Assert.Equal(ReportGenerationReviewDecision.ExecuteReview, Evaluate(revisionTen, 11));
        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, Evaluate(Snapshot(revision: 11), 11));
    }

    [Fact]
    public void ChangedSectionSelectionInvalidatesSnapshot()
    {
        var snapshot = Snapshot();

        var decision = ReportGenerationReviewPolicy.Evaluate(
            snapshot, _projectId, _reportId, 10, "01;03;04");

        Assert.Equal(ReportGenerationReviewDecision.ExecuteReview, decision);
    }

    [Fact]
    public void SectionSelectionSignatureIsIndependentOfLinkOrder()
    {
        var report = new Report();
        var first = new InspectionSection { Id = Guid.Parse("10000000-0000-0000-0000-000000000000") };
        var second = new InspectionSection { Id = Guid.Parse("20000000-0000-0000-0000-000000000000") };
        report.SelectSection(second);
        report.SelectSection(first);
        var reversed = new Report();
        reversed.SelectSection(first);
        reversed.SelectSection(second);

        Assert.Equal(
            ReportReviewSelection.CreateSignature(report),
            ReportReviewSelection.CreateSignature(reversed));
    }

    private ReportGenerationReviewDecision Evaluate(
        ReportReviewSnapshot? snapshot,
        long revision = 10) =>
        ReportGenerationReviewPolicy.Evaluate(
            snapshot, _projectId, _reportId, revision, Selection);

    private ReportReviewSnapshot Snapshot(
        IReadOnlyList<ReviewIssue>? issues = null,
        bool incomplete = false,
        long revision = 10) =>
        new(
            _projectId,
            _reportId,
            revision,
            DateTimeOffset.UtcNow,
            new ReviewResult(
                issues ?? [],
                incomplete
                    ? [new ReviewRuleFailure("test.failure", "Falha", "Exception", "Falhou")]
                    : [],
                new ReviewMetrics(0, 0, 0, 0)))
        {
            SectionSelectionSignature = Selection
        };

    private static ReviewIssue Issue(ReviewSeverity severity) => new()
    {
        RuleId = "test." + severity,
        Severity = severity,
        Title = severity.ToString(),
        Description = severity.ToString()
    };
}
