namespace GeoAutoDocs.Core.Review;

public interface IReportReviewRule
{
    string Id { get; }
    string Name { get; }

    IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default);
}

public interface IReportReviewLogger
{
    void RuleFailed(string ruleId, string ruleName, Exception exception);
}

public sealed class NullReportReviewLogger : IReportReviewLogger
{
    public static NullReportReviewLogger Instance { get; } = new();

    private NullReportReviewLogger()
    {
    }

    public void RuleFailed(string ruleId, string ruleName, Exception exception)
    {
    }
}

public interface IReviewFileSystem
{
    bool FileExists(string path);
}
