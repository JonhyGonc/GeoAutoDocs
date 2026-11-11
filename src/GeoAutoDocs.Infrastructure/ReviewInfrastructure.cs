using GeoAutoDocs.Core.Review;

namespace GeoAutoDocs.Infrastructure;

public sealed class ReviewFileSystem : IReviewFileSystem
{
    public static ReviewFileSystem Instance { get; } = new();

    private ReviewFileSystem()
    {
    }

    public bool FileExists(string path) => File.Exists(path);
}

public sealed class LocalReportReviewLogger : IReportReviewLogger
{
    private readonly LocalLogger _logger;

    public LocalReportReviewLogger(LocalLogger logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void RuleFailed(string ruleId, string ruleName, Exception exception) =>
        _logger.Error($"Falha na regra de revisão '{ruleId}' ({ruleName}).", exception);
}
