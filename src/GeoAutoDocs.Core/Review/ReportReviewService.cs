namespace GeoAutoDocs.Core.Review;

public sealed class ReportReviewService
{
    private readonly IReadOnlyList<IReportReviewRule> _rules;
    private readonly IReportReviewLogger _logger;

    public ReportReviewService(
        IEnumerable<IReportReviewRule> rules,
        IReportReviewLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToList();
        if (_rules.Any(x => x is null))
            throw new ArgumentException("A lista de regras não pode conter valores nulos.", nameof(rules));
        if (_rules.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != _rules.Count)
            throw new ArgumentException("Os identificadores das regras devem ser únicos.", nameof(rules));
        _logger = logger ?? NullReportReviewLogger.Instance;
    }

    public ReviewResult Review(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<ReviewIssue>();
        var failures = new List<ReviewRuleFailure>();

        foreach (var rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var evaluated = rule.Evaluate(context, cancellationToken)
                    ?? throw new InvalidOperationException("A regra retornou uma coleção nula.");
                foreach (var issue in evaluated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (issue is null)
                        throw new InvalidOperationException("A regra retornou um item nulo.");
                    if (!string.Equals(issue.RuleId, rule.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"A regra '{rule.Id}' retornou uma issue com RuleId '{issue.RuleId}'.");
                    issues.Add(issue);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.RuleFailed(rule.Id, rule.Name, exception);
                failures.Add(new ReviewRuleFailure(
                    rule.Id,
                    rule.Name,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var orderedIssues = issues
            .OrderBy(x => x.Severity)
            .ThenBy(x => OrderOf(context, x.LocationId, true))
            .ThenBy(x => OrderOf(context, x.SectionId, false))
            .ThenBy(x => x.Meterage ?? decimal.MinValue)
            .ThenBy(x => x.RuleId, StringComparer.Ordinal)
            .ThenBy(x => x.OccurrenceId)
            .ThenBy(x => x.CaptureId)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();
        return new ReviewResult(orderedIssues, failures, context.CreateMetrics());
    }

    private static int OrderOf(ReportReviewContext context, Guid? id, bool location)
    {
        if (id is null) return int.MinValue;
        return location
            ? context.GetLocationOrder(id.Value)
            : context.GetSectionOrder(id.Value);
    }
}

public static class ReportReviewRules
{
    public static IReadOnlyList<IReportReviewRule> CreateLegacy() =>
    [
        new Rules.ReportRequirementsRule(),
        new Rules.FileReferenceRule(),
        new Rules.EvidenceCoverageRule()
    ];

    public static IReadOnlyList<IReportReviewRule> CreateDefault() =>
    [
        .. CreateLegacy(),
        new Rules.StructureRule(),
        new Rules.OccurrenceValidityRule(),
        new Rules.DuplicateOccurrenceRule(),
        new Rules.TextQualityRule(),
        new Rules.SectionSequenceRule(),
        new Rules.SectionContinuityRule(),
        new Rules.NamingConsistencyRule()
    ];
}
