using GeoAutoDocs.Core;
using GeoAutoDocs.Core.Review;

namespace GeoAutoDocs.Infrastructure;

public sealed record ReportValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int SelectedSections,
    int Occurrences,
    int Evidences)
{
    public bool CanGenerate => Errors.Count == 0;
}

public static class ReportValidation
{
    public static ReportValidationResult Validate(Project project, Report report, string? templatePath)
    {
        var context = new ReportReviewContext(
            project,
            report,
            templatePath,
            ReviewFileSystem.Instance);
        var result = new ReportReviewService(ReportReviewRules.CreateLegacy()).Review(context);

        var errors = result.Issues
            .Where(x => x.Severity == ReviewSeverity.Error)
            .Select(x => x.Description)
            .Concat(result.RuleFailures.Select(x =>
                $"Não foi possível concluir a validação '{x.RuleName}': {x.Message}"))
            .Distinct()
            .ToList();
        var warnings = result.Issues
            .Where(x => x.Severity == ReviewSeverity.Warning)
            .Select(x => x.Description)
            .Distinct()
            .ToList();

        return new ReportValidationResult(
            errors,
            warnings,
            result.Metrics.SelectedSections,
            result.Metrics.Occurrences,
            result.Metrics.Evidences);
    }

    public static IEnumerable<(Location Location, InspectionSection Section)> OrderedSections(
        Project project,
        Report report)
    {
        var context = new ReportReviewContext(
            project,
            report,
            report.TemplatePath,
            ReviewFileSystem.Instance);
        return context.SelectedSections.Select(x => (x.Location, x.Section));
    }
}
