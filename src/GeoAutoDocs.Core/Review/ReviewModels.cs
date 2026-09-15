namespace GeoAutoDocs.Core.Review;

public enum ReviewSeverity
{
    Error = 0,
    Warning = 1,
    Info = 2
}

public enum ReviewNavigationArea
{
    Report,
    Inspection,
    PendingCaptures
}

public sealed record ReviewIssue
{
    public required string RuleId { get; init; }
    public required ReviewSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public ReviewNavigationArea NavigationArea { get; init; } = ReviewNavigationArea.Report;

    public Guid? LocationId { get; init; }
    public Guid? SectionId { get; init; }
    public Guid? OccurrenceId { get; init; }
    public Guid? CaptureId { get; init; }
    public Guid? VideoReferenceId { get; init; }
    public Guid? EvidenceId { get; init; }
    public string? ReportField { get; init; }
    public decimal? Meterage { get; init; }
    public string? SuggestedFix { get; init; }
    public bool CanBeAcceptedAfterVerification { get; init; }
}

public sealed record ReviewRuleFailure(
    string RuleId,
    string RuleName,
    string ExceptionType,
    string Message);

public sealed record ReviewMetrics(
    int SelectedSections,
    int Occurrences,
    int Evidences,
    int PendingCaptures);

public sealed class ReviewResult
{
    public ReviewResult(
        IReadOnlyList<ReviewIssue> issues,
        IReadOnlyList<ReviewRuleFailure> ruleFailures,
        ReviewMetrics metrics)
    {
        Issues = issues;
        RuleFailures = ruleFailures;
        Metrics = metrics;
    }

    public IReadOnlyList<ReviewIssue> Issues { get; }
    public IReadOnlyList<ReviewRuleFailure> RuleFailures { get; }
    public ReviewMetrics Metrics { get; }

    public int ErrorCount => Issues.Count(x => x.Severity == ReviewSeverity.Error);
    public int WarningCount => Issues.Count(x => x.Severity == ReviewSeverity.Warning);
    public int InfoCount => Issues.Count(x => x.Severity == ReviewSeverity.Info);
    public bool HasErrors => ErrorCount > 0;
    public bool IsComplete => RuleFailures.Count == 0;
    public bool HasRuleFailures => RuleFailures.Count > 0;
}
