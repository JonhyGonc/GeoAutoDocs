namespace GeoAutoDocs.Core.Review.Rules;

public sealed class DuplicateOccurrenceRule : IReportReviewRule
{
    private sealed record Candidate(
        Guid AnomalyTypeId,
        string AnomalyName,
        decimal Meterage,
        Occurrence Occurrence,
        ReviewSectionContext Section);

    public string Id => "occurrence.duplicates";
    public string Name => "Possíveis ocorrências duplicadas";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Options.CheckDuplicateOccurrences) yield break;

        foreach (var section in context.SelectedSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = section.Section.Occurrences
                .SelectMany(occurrence => occurrence.Anomalies
                    .GroupBy(x => x.AnomalyTypeId)
                    .Select(x => new { Anomaly = x.First(), Occurrence = occurrence }))
                .Select(x => PrimaryMeterage(x.Occurrence) is decimal meterage
                    ? new Candidate(
                        x.Anomaly.AnomalyTypeId,
                        x.Anomaly.DisplayName,
                        meterage,
                        x.Occurrence,
                        section)
                    : null)
                .Where(x => x is not null)
                .Cast<Candidate>()
                .GroupBy(x => x.AnomalyTypeId)
                .OrderBy(x => x.Key);

            foreach (var typeGroup in candidates)
            {
                var ordered = typeGroup
                    .OrderBy(x => x.Meterage)
                    .ThenBy(x => x.Occurrence.Id)
                    .ToList();
                var clusterStart = 0;
                for (var index = 1; index <= ordered.Count; index++)
                {
                    var continues = index < ordered.Count &&
                        ordered[index].Meterage - ordered[index - 1].Meterage <=
                        context.Options.DuplicateOccurrenceToleranceMeters;
                    if (continues) continue;

                    var cluster = ordered
                        .Skip(clusterStart)
                        .Take(index - clusterStart)
                        .GroupBy(x => x.Occurrence.Id)
                        .Select(x => x.First())
                        .ToList();
                    if (cluster.Count > 1)
                    {
                        var first = cluster[0];
                        var last = cluster[^1];
                        yield return new ReviewIssue
                        {
                            RuleId = Id,
                            Severity = ReviewSeverity.Warning,
                            Title = "Possíveis ocorrências duplicadas",
                            Description =
                                $"{cluster.Count} ocorrências do tipo “{first.AnomalyName}” foram encontradas " +
                                $"entre {first.Meterage:0.##} m e {last.Meterage:0.##} m.",
                            LocationId = section.Location.Id,
                            SectionId = section.Section.Id,
                            OccurrenceId = first.Occurrence.Id,
                            VideoReferenceId = first.Occurrence.VideoReferenceId,
                            Meterage = first.Meterage,
                            SuggestedFix = "Confira as ocorrências próximas antes de remover ou manter qualquer registro."
                        };
                    }

                    clusterStart = index;
                }
            }
        }
    }

    private static decimal? PrimaryMeterage(Occurrence occurrence) => occurrence.LocationMode switch
    {
        OccurrenceLocationMode.Point or OccurrenceLocationMode.Range or OccurrenceLocationMode.From =>
            occurrence.Meterage,
        OccurrenceLocationMode.MultiPoint when occurrence.MeteragePoints.Count > 0 =>
            occurrence.MeteragePoints.Min(x => x.Meterage),
        _ => null
    };
}
