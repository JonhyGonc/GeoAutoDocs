namespace GeoAutoDocs.Core.Review;

public sealed record ReviewSectionContext(
    Location Location,
    InspectionSection Section,
    int LocationOrder,
    int SectionOrder);

public sealed class ReportReviewContext
{
    private readonly IReadOnlyDictionary<Guid, ReviewSectionContext> _sectionsById;
    private readonly IReadOnlyDictionary<Guid, VideoReference> _videosById;
    private readonly IReadOnlyDictionary<Guid, Capture> _capturesById;
    private readonly IReadOnlyDictionary<Guid, AnomalyDefinition> _anomalyDefinitionsById;
    private readonly IReadOnlyDictionary<Guid, ReviewSectionContext> _occurrenceSectionsById;

    public ReportReviewContext(
        Project project,
        Report report,
        string? templatePath,
        IReviewFileSystem fileSystem,
        ReviewOptions? options = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Report = report ?? throw new ArgumentNullException(nameof(report));
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        Options = options ?? ReviewOptions.Default;
        Options.Validate();
        TemplatePath = templatePath;

        var selectedIds = report.SectionLinks.Select(x => x.SectionId).ToHashSet();
        SelectedSections = project.Locations
            .Select((location, locationIndex) => (location, locationIndex))
            .OrderBy(x => x.location.Order)
            .ThenBy(x => x.location.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.location.Id)
            .SelectMany(location => location.location.Sections
                .Select((section, sectionIndex) => (section, sectionIndex))
                .Where(x => selectedIds.Contains(x.section.Id))
                .OrderBy(x => x.section.Order)
                .ThenBy(x => x.section.Name, StringComparer.CurrentCulture)
                .ThenBy(x => x.section.Id)
                .Select(x => new ReviewSectionContext(
                    location.location,
                    x.section,
                    location.location.Order,
                    x.section.Order)))
            .ToList();

        _sectionsById = SelectedSections.ToDictionary(x => x.Section.Id);
        _videosById = SelectedSections
            .SelectMany(x => x.Section.VideoReferences)
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First());
        _capturesById = project.Captures
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First());

        var knownDefinitions = AnomalyCatalog.Defaults
            .Concat(SelectedSections
                .SelectMany(x => x.Section.Occurrences)
                .SelectMany(x => x.Anomalies)
                .Where(x => x.AnomalyType is not null)
                .Select(x => x.AnomalyType))
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First());
        _anomalyDefinitionsById = knownDefinitions;

        _occurrenceSectionsById = SelectedSections
            .SelectMany(section => section.Section.Occurrences.Select(occurrence => (occurrence.Id, section)))
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First().section);
        Occurrences = SelectedSections
            .SelectMany(x => x.Section.Occurrences
                .OrderBy(occurrence => occurrence.Order)
                .ThenBy(occurrence => occurrence.Meterage)
                .ThenBy(occurrence => occurrence.Id))
            .ToList();
        Evidences = Occurrences.SelectMany(x => x.Evidence).ToList();
        PendingCaptures = project.Captures
            .Where(x => selectedIds.Contains(x.SectionId) && x.Status == CaptureStatus.Pending)
            .ToList();
    }

    public Project Project { get; }
    public Report Report { get; }
    public string? TemplatePath { get; }
    public IReviewFileSystem FileSystem { get; }
    public ReviewOptions Options { get; }
    public IReadOnlyList<ReviewSectionContext> SelectedSections { get; }
    public IReadOnlyList<Occurrence> Occurrences { get; }
    public IReadOnlyList<Evidence> Evidences { get; }
    public IReadOnlyList<Capture> PendingCaptures { get; }

    public bool TryGetSection(Guid id, out ReviewSectionContext section) =>
        _sectionsById.TryGetValue(id, out section!);

    public bool TryGetOccurrenceSection(Guid occurrenceId, out ReviewSectionContext section) =>
        _occurrenceSectionsById.TryGetValue(occurrenceId, out section!);

    public bool TryGetVideo(Guid id, out VideoReference video) =>
        _videosById.TryGetValue(id, out video!);

    public bool TryGetCapture(Guid id, out Capture capture) =>
        _capturesById.TryGetValue(id, out capture!);

    public bool TryGetAnomalyDefinition(Guid id, out AnomalyDefinition definition) =>
        _anomalyDefinitionsById.TryGetValue(id, out definition!);

    public ReviewMetrics CreateMetrics() => new(
        SelectedSections.Count,
        Occurrences.Count,
        Evidences.Count,
        PendingCaptures.Count);
}
