namespace GeoAutoDocs.Core.Review;

public sealed record ReviewSectionContext(
    Location Location,
    InspectionSection Section,
    int LocationOrder,
    int SectionOrder);

public sealed class ReportReviewContext
{
    private readonly IReadOnlyDictionary<Guid, ReviewSectionContext> _sectionsById;
    private readonly IReadOnlyDictionary<Guid, Location> _locationsById;
    private readonly IReadOnlyDictionary<Guid, VideoReference> _videosById;
    private readonly IReadOnlyDictionary<Guid, Capture> _capturesById;
    private readonly IReadOnlyDictionary<Guid, AnomalyDefinition> _anomalyDefinitionsById;
    private readonly IReadOnlyDictionary<Guid, ReviewSectionContext> _occurrenceSectionsById;
    private readonly IReadOnlyDictionary<Guid, Occurrence> _occurrencesById;
    private readonly IReadOnlyDictionary<Guid, int> _locationOrdersById;
    private readonly IReadOnlyDictionary<Guid, int> _sectionOrdersById;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Capture>> _pendingCapturesBySectionId;

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
            .OrderBy(location => location.Order)
            .SelectMany(location => location.Sections
                .Where(section => selectedIds.Contains(section.Id))
                .OrderBy(section => section.Order)
                .Select(section => new ReviewSectionContext(
                    location,
                    section,
                    location.Order,
                    section.Order)))
            .ToList();

        _sectionsById = SelectedSections.ToDictionary(x => x.Section.Id);
        _locationsById = SelectedSections
            .GroupBy(x => x.Location.Id)
            .ToDictionary(x => x.Key, x => x.First().Location);
        // Ranks da ordem efetiva preservam os empates de Order exatamente como
        // a exportação: sequência original de criação, sem desempate pelo nome.
        _locationOrdersById = SelectedSections
            .Select(x => x.Location.Id)
            .Distinct()
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        _sectionOrdersById = SelectedSections
            .Select((section, index) => (section.Section.Id, index))
            .ToDictionary(x => x.Id, x => x.index);
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
        _occurrencesById = Occurrences
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First());
        Evidences = Occurrences.SelectMany(x => x.Evidence).ToList();
        PendingCaptures = project.Captures
            .Where(x => selectedIds.Contains(x.SectionId) && x.Status == CaptureStatus.Pending)
            .OrderBy(x => x.SectionId)
            .ThenBy(x => x.Order)
            .ThenBy(x => x.Id)
            .ToList();
        _pendingCapturesBySectionId = PendingCaptures
            .GroupBy(x => x.SectionId)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<Capture>)x.ToList());
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

    public bool TryGetLocation(Guid id, out Location location) =>
        _locationsById.TryGetValue(id, out location!);

    public IReadOnlyList<Capture> GetPendingCaptures(Guid sectionId) =>
        _pendingCapturesBySectionId.TryGetValue(sectionId, out var captures) ? captures : [];

    public bool TryGetOccurrenceSection(Guid occurrenceId, out ReviewSectionContext section) =>
        _occurrenceSectionsById.TryGetValue(occurrenceId, out section!);

    public bool TryGetOccurrence(Guid occurrenceId, out Occurrence occurrence) =>
        _occurrencesById.TryGetValue(occurrenceId, out occurrence!);

    public int GetLocationOrder(Guid id) =>
        _locationOrdersById.TryGetValue(id, out var order) ? order : int.MaxValue;

    public int GetSectionOrder(Guid id) =>
        _sectionOrdersById.TryGetValue(id, out var order) ? order : int.MaxValue;

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
