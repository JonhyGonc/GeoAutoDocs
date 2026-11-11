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

    public bool ClearVerified()
    {
        if (_verified.Count == 0) return false;
        _verified.Clear();
        Version++;
        return true;
    }

    public IEnumerable<ReviewIssue> PendingIssues =>
        Snapshot.Result.Issues.Where(issue => !IsVerified(issue));

    public int UnverifiedInfoCount => PendingIssues.Count(issue => issue.Severity == ReviewSeverity.Info);
    public int UnverifiedIssueCount => PendingIssues.Count();

    public int BlockingErrorCount => Snapshot.Result.Issues.Count(issue =>
        issue.Severity == ReviewSeverity.Error &&
        !IsVerified(issue));

    public int UnverifiedWarningCount => Snapshot.Result.Issues.Count(issue =>
        issue.Severity == ReviewSeverity.Warning && !IsVerified(issue));
}
