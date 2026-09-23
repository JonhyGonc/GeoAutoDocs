namespace GeoAutoDocs.Core.Review;

public enum ReviewIssueFilter
{
    All,
    Errors,
    Warnings,
    Information
}

public static class ReviewIssueFiltering
{
    public static IReadOnlyList<ReviewIssue> Apply(
        IEnumerable<ReviewIssue> issues,
        ReviewIssueFilter filter)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return issues.Where(issue => filter switch
        {
            ReviewIssueFilter.Errors => issue.Severity == ReviewSeverity.Error,
            ReviewIssueFilter.Warnings => issue.Severity == ReviewSeverity.Warning,
            ReviewIssueFilter.Information => issue.Severity == ReviewSeverity.Info,
            _ => true
        }).ToList();
    }
}

public sealed record ReportReviewSnapshot(
    Guid ProjectId,
    Guid ReportId,
    long ProjectRevision,
    DateTimeOffset CompletedAt,
    ReviewResult Result)
{
    public string SectionSelectionSignature { get; init; } = string.Empty;

    public bool AppliesTo(Guid projectId, Guid reportId) =>
        ProjectId == projectId && ReportId == reportId;

    public bool IsStale(Guid projectId, Guid reportId, long currentRevision) =>
        AppliesTo(projectId, reportId) && ProjectRevision != currentRevision;

    public bool IsCurrent(
        Guid projectId,
        Guid reportId,
        long currentRevision,
        string sectionSelectionSignature) =>
        AppliesTo(projectId, reportId) &&
        ProjectRevision == currentRevision &&
        string.Equals(
            SectionSelectionSignature,
            sectionSelectionSignature,
            StringComparison.Ordinal);
}

public static class ReportReviewSelection
{
    public static string CreateSignature(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return string.Join(
            ";",
            report.SectionLinks
                .Select(x => x.SectionId)
                .Distinct()
                .OrderBy(x => x)
                .Select(x => x.ToString("N")));
    }
}

public enum ReportGenerationReviewDecision
{
    ExecuteReview,
    AllowGeneration,
    ConfirmWarnings,
    BlockErrors,
    BlockIncomplete
}

public static class ReportGenerationReviewPolicy
{
    public static ReportGenerationReviewDecision Evaluate(
        ReportReviewSnapshot? snapshot,
        Guid projectId,
        Guid reportId,
        long currentRevision,
        string sectionSelectionSignature,
        bool warningsAccepted = false,
        ReviewVerificationState? verification = null)
    {
        if (snapshot is null ||
            !snapshot.IsCurrent(
                projectId,
                reportId,
                currentRevision,
                sectionSelectionSignature))
            return ReportGenerationReviewDecision.ExecuteReview;

        if (!snapshot.Result.IsComplete)
            return ReportGenerationReviewDecision.BlockIncomplete;
        var verified = ReferenceEquals(verification?.Snapshot, snapshot) ? verification : null;
        if ((verified?.BlockingErrorCount ?? snapshot.Result.ErrorCount) > 0)
            return ReportGenerationReviewDecision.BlockErrors;
        if ((verified?.UnverifiedWarningCount ?? snapshot.Result.WarningCount) > 0 && !warningsAccepted)
            return ReportGenerationReviewDecision.ConfirmWarnings;
        return ReportGenerationReviewDecision.AllowGeneration;
    }
}

public enum ReportReviewExecutionStatus
{
    Completed,
    Cancelled,
    Failed,
    Busy
}

public sealed record ReportReviewExecutionOutcome(
    ReportReviewExecutionStatus Status,
    ReportReviewSnapshot? Snapshot)
{
    public static ReportReviewExecutionOutcome Completed(ReportReviewSnapshot snapshot) =>
        new(ReportReviewExecutionStatus.Completed, snapshot);

    public static ReportReviewExecutionOutcome Cancelled() =>
        new(ReportReviewExecutionStatus.Cancelled, null);

    public static ReportReviewExecutionOutcome Failed() =>
        new(ReportReviewExecutionStatus.Failed, null);

    public static ReportReviewExecutionOutcome Busy() =>
        new(ReportReviewExecutionStatus.Busy, null);
}

public sealed record ReviewNavigationTarget(
    ReviewNavigationArea Area,
    Guid? LocationId,
    Guid? SectionId,
    Guid? OccurrenceId,
    Guid? CaptureId,
    Guid? VideoReferenceId,
    Guid? EvidenceId,
    string? ReportField)
{
    public static ReviewNavigationTarget FromIssue(ReviewIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new ReviewNavigationTarget(
            issue.NavigationArea,
            issue.LocationId,
            issue.SectionId,
            issue.OccurrenceId,
            issue.CaptureId,
            issue.VideoReferenceId,
            issue.EvidenceId,
            issue.ReportField);
    }
}
