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
    public bool AppliesTo(Guid projectId, Guid reportId) =>
        ProjectId == projectId && ReportId == reportId;

    public bool IsStale(Guid projectId, Guid reportId, long currentRevision) =>
        AppliesTo(projectId, reportId) && ProjectRevision != currentRevision;
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
