namespace GeoAutoDocs.Core.Review;

// A conferência pertence a uma instância do snapshot, nunca ao banco ou a outro resultado.
public sealed class ReviewVerificationState(ReportReviewSnapshot snapshot)
{
    private readonly HashSet<ReviewIssue> _issues = snapshot.Result.Issues.ToHashSet();
    private readonly HashSet<ReviewIssue> _verified = [];

    public ReportReviewSnapshot Snapshot { get; } = snapshot;
    public int Version { get; private set; }
    public int VerifiedCount => _verified.Count;
    public bool IsVerified(ReviewIssue issue) => _verified.Contains(issue);

    public bool SetVerified(ReviewIssue issue, bool verified)
    {
        if (!_issues.Contains(issue)) return false;
        var changed = verified ? _verified.Add(issue) : _verified.Remove(issue);
        if (changed) Version++;
        return changed;
    }

    public int BlockingErrorCount => Snapshot.Result.Issues.Count(issue =>
        issue.Severity == ReviewSeverity.Error &&
        (!issue.CanBeAcceptedAfterVerification || !IsVerified(issue)));

    public int UnverifiedWarningCount => Snapshot.Result.Issues.Count(issue =>
        issue.Severity == ReviewSeverity.Warning && !IsVerified(issue));
}
