using GeoAutoDocs.Core.Review;
using GeoAutoDocs.Core.Review.Rules;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class ReviewVerificationTests
{
    [Theory]
    [InlineData(ReviewSeverity.Error)]
    [InlineData(ReviewSeverity.Warning)]
    [InlineData(ReviewSeverity.Info)]
    public void VerificationIsExplicitReversibleAndDoesNotChangeRawResult(ReviewSeverity severity)
    {
        var issue = Issue(severity);
        var snapshot = Snapshot([issue]);
        var state = new ReviewVerificationState(snapshot);

        Assert.False(state.IsVerified(issue));
        Assert.True(state.SetVerified(issue, true));
        Assert.True(state.IsVerified(issue));
        Assert.Equal(1, state.VerifiedCount);
        Assert.Equal(1, state.Version);
        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, Evaluate(snapshot, state));
        Assert.Single(snapshot.Result.Issues);
        Assert.Equal(severity == ReviewSeverity.Error, snapshot.Result.HasErrors);
        Assert.False(state.SetVerified(issue, true));
        Assert.Equal(1, state.Version);
        Assert.True(state.SetVerified(issue, false));
        Assert.False(state.IsVerified(issue));
        Assert.Equal(0, state.VerifiedCount);
        Assert.Equal(2, state.Version);
        Assert.Equal(severity switch
        {
            ReviewSeverity.Error => ReportGenerationReviewDecision.BlockErrors,
            ReviewSeverity.Warning => ReportGenerationReviewDecision.ConfirmWarnings,
            _ => ReportGenerationReviewDecision.AllowGeneration
        }, Evaluate(snapshot, state));
    }

    [Fact]
    public void VerifiedErrorIsResolvedInReviewAndNoLongerBlocksStructuredPolicy()
    {
        var issue = Issue(ReviewSeverity.Error);
        var snapshot = Snapshot([issue]);
        var state = new ReviewVerificationState(snapshot);
        state.SetVerified(issue, true);

        Assert.True(state.IsVerified(issue));
        Assert.Equal(0, state.BlockingErrorCount);
        Assert.Empty(state.PendingIssues);
        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, Evaluate(snapshot, state));
    }

    [Fact]
    public void IncompleteReviewCannotBeApprovedByVerification()
    {
        var issue = Issue(ReviewSeverity.Warning);
        var snapshot = Snapshot([issue], incomplete: true);
        var state = new ReviewVerificationState(snapshot);
        state.SetVerified(issue, true);

        Assert.Equal(ReportGenerationReviewDecision.BlockIncomplete, Evaluate(snapshot, state));
    }

    [Fact]
    public void VerificationFromAnotherSnapshotIsIgnoredEvenForIdenticalIssues()
    {
        var issue = Issue(ReviewSeverity.Warning);
        var previous = Snapshot([issue]);
        var current = previous with { Result = new ReviewResult([issue], [], previous.Result.Metrics) };
        var state = new ReviewVerificationState(previous);
        state.SetVerified(issue, true);

        Assert.Equal(ReportGenerationReviewDecision.ConfirmWarnings, Evaluate(current, state));
        Assert.Equal(0, new ReviewVerificationState(current).VerifiedCount);
    }

    [Fact]
    public void VerificationNeverApprovesChangedRevisionSelectionOrProject()
    {
        var issue = Issue(ReviewSeverity.Error);
        var snapshot = Snapshot([issue]);
        var state = new ReviewVerificationState(snapshot);
        state.SetVerified(issue, true);

        Assert.Equal(ReportGenerationReviewDecision.ExecuteReview,
            ReportGenerationReviewPolicy.Evaluate(snapshot, snapshot.ProjectId, snapshot.ReportId,
                11, "selection", verification: state));
        Assert.Equal(ReportGenerationReviewDecision.ExecuteReview,
            ReportGenerationReviewPolicy.Evaluate(snapshot, snapshot.ProjectId, snapshot.ReportId,
                10, "different-selection", verification: state));
        Assert.Equal(ReportGenerationReviewDecision.ExecuteReview,
            ReportGenerationReviewPolicy.Evaluate(snapshot, Guid.NewGuid(), snapshot.ReportId,
                10, "selection", verification: state));
    }

    [Fact]
    public void OnlyWarningsStillUnverifiedRequireConfirmation()
    {
        var first = Issue(ReviewSeverity.Warning);
        var second = first with { Title = "Outro aviso" };
        var snapshot = Snapshot([first, second]);
        var state = new ReviewVerificationState(snapshot);
        state.SetVerified(first, true);

        Assert.Equal(1, state.UnverifiedWarningCount);
        Assert.Equal(ReportGenerationReviewDecision.ConfirmWarnings, Evaluate(snapshot, state));
        state.SetVerified(second, true);
        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, Evaluate(snapshot, state));
    }

    [Fact]
    public void ForeignIssueCannotBeMarked()
    {
        var state = new ReviewVerificationState(Snapshot([]));

        Assert.False(state.SetVerified(Issue(ReviewSeverity.Info), true));
        Assert.Equal(0, state.VerifiedCount);
    }

    [Fact]
    public void HumanResolutionHandlesAllOccurrenceErrorsWithoutChangingTheirData()
    {
        var fixture = ReviewFixture.Create();
        fixture.Section.InvestigatedMeters = 20;
        var boundary = fixture.NewOccurrence(20.01m, "Raiz");
        var invalid = fixture.NewOccurrence(-1, "Raiz");
        fixture.Section.Occurrences.Add(boundary);
        fixture.Section.Occurrences.Add(invalid);
        var result = fixture.Review(new OccurrenceValidityRule());
        var snapshot = Snapshot(result.Issues);
        var state = new ReviewVerificationState(snapshot);

        Assert.Contains(result.Issues, x => x.Title == "Ocorrência fora dos limites do trecho");
        Assert.Contains(result.Issues, x => x.Title == "Ocorrência inválida");
        foreach (var issue in result.Issues) state.SetVerified(issue, true);

        Assert.Empty(state.PendingIssues);
        Assert.Equal(0, state.BlockingErrorCount);
        Assert.Equal(ReportGenerationReviewDecision.AllowGeneration, Evaluate(snapshot, state));
        Assert.Equal(20.01m, boundary.Meterage);
        Assert.Equal(-1m, invalid.Meterage);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void VerifiedIssuesDisappearFromEveryFilterAndPendingCounter()
    {
        var error = Issue(ReviewSeverity.Error);
        var warning = Issue(ReviewSeverity.Warning);
        var info = Issue(ReviewSeverity.Info);
        var state = new ReviewVerificationState(Snapshot([error, warning, info]));
        state.SetVerified(error, true);
        Assert.Single(ReviewIssueFiltering.Apply(state.PendingIssues, ReviewIssueFilter.Warnings));
        Assert.Empty(ReviewIssueFiltering.Apply(state.PendingIssues, ReviewIssueFilter.Errors));
        Assert.Equal(2, state.UnverifiedIssueCount);

        state.SetVerified(warning, true);
        state.SetVerified(info, true);

        Assert.Equal(0, state.BlockingErrorCount);
        Assert.Equal(0, state.UnverifiedWarningCount);
        Assert.Equal(0, state.UnverifiedInfoCount);
        Assert.Equal(0, state.UnverifiedIssueCount);
        foreach (var filter in Enum.GetValues<ReviewIssueFilter>())
            Assert.Empty(ReviewIssueFiltering.Apply(state.PendingIssues, filter));
    }

    [Fact]
    public void ClearVerificationRestoresAllIssuesCountsAndGenerationGate()
    {
        var error = Issue(ReviewSeverity.Error);
        var warning = Issue(ReviewSeverity.Warning);
        var info = Issue(ReviewSeverity.Info);
        var snapshot = Snapshot([error, warning, info]);
        var state = new ReviewVerificationState(snapshot);
        Assert.False(state.ClearVerified());
        foreach (var issue in snapshot.Result.Issues) state.SetVerified(issue, true);
        var version = state.Version;

        Assert.True(state.ClearVerified());

        Assert.Equal(version + 1, state.Version);
        Assert.Equal(0, state.VerifiedCount);
        Assert.Equal(snapshot.Result.Issues, state.PendingIssues);
        Assert.Equal(1, state.BlockingErrorCount);
        Assert.Equal(1, state.UnverifiedWarningCount);
        Assert.Equal(1, state.UnverifiedInfoCount);
        Assert.Equal(ReportGenerationReviewDecision.BlockErrors, Evaluate(snapshot, state));
        Assert.False(state.ClearVerified());
        Assert.Equal(version + 1, state.Version);
    }

    private static ReviewIssue Issue(ReviewSeverity severity) => new()
    {
        RuleId = "test.verification",
        Severity = severity,
        Title = "Apontamento",
        Description = "Conferir"
    };

    private static ReportReviewSnapshot Snapshot(IReadOnlyList<ReviewIssue> issues, bool incomplete = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 10, DateTimeOffset.UtcNow,
            new ReviewResult(issues,
                incomplete ? [new ReviewRuleFailure("test.failure", "Falha", "Exception", "Falhou")] : [],
                new ReviewMetrics(0, 0, 0, 0)))
        { SectionSelectionSignature = "selection" };

    private static ReportGenerationReviewDecision Evaluate(
        ReportReviewSnapshot snapshot, ReviewVerificationState state) =>
        ReportGenerationReviewPolicy.Evaluate(snapshot, snapshot.ProjectId, snapshot.ReportId,
            10, "selection", verification: state);
}
