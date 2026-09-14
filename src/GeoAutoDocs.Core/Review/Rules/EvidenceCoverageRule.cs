namespace GeoAutoDocs.Core.Review.Rules;

public sealed class EvidenceCoverageRule : IReportReviewRule
{
    public string Id => "report.coverage";
    public string Name => "Cobertura de evidências e capturas";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var occurrence in context.Occurrences.Where(x => x.Evidence.Count == 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.TryGetSection(occurrence.InspectionSectionId, out var section);
            var sectionName = section?.Section.Name ?? "Trecho selecionado";
            yield return new ReviewIssue
            {
                RuleId = Id,
                Severity = ReviewSeverity.Warning,
                Title = "Anomalia sem evidência",
                Description = $"{sectionName} possui anomalia sem evidência.",
                NavigationArea = ReviewNavigationArea.Inspection,
                LocationId = section?.Location.Id,
                SectionId = section?.Section.Id ?? occurrence.InspectionSectionId,
                OccurrenceId = occurrence.Id,
                VideoReferenceId = occurrence.VideoReferenceId,
                Meterage = occurrence.Meterage
            };
        }

        foreach (var section in context.SelectedSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = context.PendingCaptures
                .Where(x => x.SectionId == section.Section.Id)
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Id)
                .ToList();
            if (pending.Count == 0) continue;

            yield return new ReviewIssue
            {
                RuleId = Id,
                Severity = ReviewSeverity.Warning,
                Title = "Capturas pendentes",
                Description =
                    $"{section.Section.Name} possui {pending.Count} captura(s) pendente(s); elas não entrarão no relatório.",
                NavigationArea = ReviewNavigationArea.PendingCaptures,
                LocationId = section.Location.Id,
                SectionId = section.Section.Id,
                CaptureId = pending[0].Id,
                VideoReferenceId = pending[0].VideoReferenceId
            };
        }
    }
}
