namespace GeoAutoDocs.Core;

public sealed record SectionReviewPending(int IncompleteOccurrences, int PendingCaptures)
{
    public bool HasPending => IncompleteOccurrences > 0 || PendingCaptures > 0;
}

public static class SectionReviewRules
{
    public static SectionReviewPending GetPending(Project project, InspectionSection section) => new(
        section.Occurrences.Count(x => !OccurrenceRules.IsComplete(x)),
        project.Captures.Count(x => x.SectionId == section.Id && x.Status == CaptureStatus.Pending));
}
