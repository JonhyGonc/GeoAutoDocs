namespace GeoAutoDocs.Core.Review.Rules;

public sealed class NamingConsistencyRule : IReportReviewRule
{
    private sealed record NumberedSection(
        ReviewSectionContext Context,
        string CurrentName,
        string Stem,
        string StemKey,
        int Number,
        int NumberWidth);

    public string Id => "naming.consistency";
    public string Name => "Padronização de nomenclatura";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Options.CheckNamingConsistency) yield break;

        var numberedSections = context.SelectedSections
            .Select(Parse)
            .Where(x => x is not null)
            .Cast<NumberedSection>()
            .ToList();

        foreach (var group in numberedSections
                     .GroupBy(x => x.StemKey, StringComparer.Ordinal)
                     .Where(x => x.Count() > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preferredStem = group
                .GroupBy(x => x.Stem, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => numberedSections.IndexOf(x.First()))
                .First()
                .Key;
            var commonWidth = group
                .GroupBy(x => x.NumberWidth)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => numberedSections.IndexOf(x.First()))
                .First()
                .Key;
            var expectedWidth = group.Key == "TRECHO"
                ? Math.Max(2, commonWidth)
                : commonWidth;

            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proposed = preferredStem + item.Number.ToString($"D{expectedWidth}");
                if (string.Equals(item.CurrentName, proposed, StringComparison.Ordinal))
                    continue;

                yield return new ReviewIssue
                {
                    RuleId = Id,
                    Severity = ReviewSeverity.Info,
                    Title = "Formatação de nome inconsistente",
                    Description = $"“{item.CurrentName}” usa uma formatação diferente dos demais trechos da mesma série.",
                    NavigationArea = ReviewNavigationArea.Inspection,
                    LocationId = item.Context.Location.Id,
                    SectionId = item.Context.Section.Id,
                    ReportField = nameof(InspectionSection.Name),
                    SuggestedFix = $"Padronização sugerida: “{proposed}”."
                };
            }
        }

        foreach (var occurrence in context.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.TryGetOccurrenceSection(occurrence.Id, out var section);
            foreach (var anomaly in occurrence.Anomalies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (anomaly.AnomalyType is null ||
                    string.IsNullOrWhiteSpace(anomaly.AnomalyName) ||
                    string.IsNullOrWhiteSpace(anomaly.AnomalyType.Name) ||
                    string.Equals(
                        anomaly.AnomalyName.Trim(),
                        anomaly.AnomalyType.Name.Trim(),
                        StringComparison.CurrentCulture))
                    continue;

                yield return new ReviewIssue
                {
                    RuleId = Id,
                    Severity = ReviewSeverity.Info,
                    Title = "Nome de anomalia diferente do catálogo",
                    Description =
                        $"A ocorrência registra “{anomaly.AnomalyName.Trim()}”, enquanto o catálogo atual utiliza " +
                        $"“{anomaly.AnomalyType.Name.Trim()}”.",
                    NavigationArea = ReviewNavigationArea.Inspection,
                    LocationId = section?.Location.Id,
                    SectionId = section?.Section.Id ?? occurrence.InspectionSectionId,
                    OccurrenceId = occurrence.Id,
                    VideoReferenceId = occurrence.VideoReferenceId,
                    Meterage = occurrence.Meterage,
                    ReportField = nameof(OccurrenceAnomaly.AnomalyName),
                    SuggestedFix = "Confira o texto e, se apropriado, atualize a ocorrência usando o catálogo atual."
                };
            }
        }
    }

    private static NumberedSection? Parse(ReviewSectionContext context) =>
        SectionSequenceRule.TryParseName(
            context.Section.Name,
            out var stem,
            out var stemKey,
            out var number,
            out var width)
            ? new NumberedSection(
                context,
                context.Section.Name.Trim(),
                stem,
                stemKey,
                number,
                width)
            : null;
}
