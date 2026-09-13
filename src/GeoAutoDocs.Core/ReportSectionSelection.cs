namespace GeoAutoDocs.Core;

public static class ReportSectionSelection
{
    public static void SetAll(
        Report report,
        IReadOnlyList<InspectionSection> orderedSections,
        bool selected)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(orderedSections);

        if (!selected)
        {
            report.SectionLinks.Clear();
            return;
        }

        var existing = report.SectionLinks
            .GroupBy(x => x.SectionId)
            .ToDictionary(x => x.Key, x => x.First());
        var normalized = new List<ReportSection>(orderedSections.Count);
        var seen = new HashSet<Guid>();
        foreach (var section in orderedSections)
        {
            if (!seen.Add(section.Id)) continue;
            if (!existing.TryGetValue(section.Id, out var link))
            {
                link = new ReportSection
                {
                    ReportId = report.Id,
                    SectionId = section.Id
                };
            }

            link.ReportId = report.Id;
            link.SectionId = section.Id;
            link.Section = section;
            link.Order = normalized.Count;
            normalized.Add(link);
        }

        report.SectionLinks.Clear();
        report.SectionLinks.AddRange(normalized);
    }
}
