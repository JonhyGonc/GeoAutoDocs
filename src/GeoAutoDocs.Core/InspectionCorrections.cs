using System.Text.Json.Serialization;

namespace GeoAutoDocs.Core;

public sealed class InspectionCorrectionPackage
{
    public Guid ReportId { get; set; }
    public List<InspectionCorrection> Corrections { get; set; } = [];
}

public sealed class InspectionCorrection
{
    private decimal? _meterage;
    private string? _description;

    public Guid SectionId { get; set; }
    public Guid OccurrenceId { get; set; }

    public decimal? Meterage
    {
        get => _meterage;
        set
        {
            _meterage = value;
            HasMeterage = true;
        }
    }

    public string? Description
    {
        get => _description;
        set
        {
            _description = value;
            HasDescription = true;
        }
    }

    [JsonIgnore]
    public bool HasMeterage { get; private set; }

    [JsonIgnore]
    public bool HasDescription { get; private set; }
}

public sealed class InspectionCorrectionAcknowledgement
{
    public bool Accepted { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public int AppliedCount { get; set; }
    public List<InspectionCorrectionItemAcknowledgement> Results { get; set; } = [];
}

public sealed class InspectionCorrectionItemAcknowledgement
{
    public Guid SectionId { get; set; }
    public Guid OccurrenceId { get; set; }
    public bool Applied { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public interface IInspectionCorrectionChannel
{
    void SetCorrectionHandler(
        Func<InspectionCorrectionPackage, CancellationToken, Task<InspectionCorrectionAcknowledgement>> handler);

    Task StopCorrectionSessionAsync();
}

public enum InspectionCorrectionResolutionStatus
{
    Ready,
    Unchanged,
    Invalid,
    NotFound
}

public sealed class InspectionCorrectionResolution
{
    public required InspectionCorrection Correction { get; init; }
    public InspectionCorrectionResolutionStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public string SectionName { get; init; } = string.Empty;
    public decimal? CurrentMeterage { get; init; }
    public string CurrentDescription { get; init; } = string.Empty;
    public decimal? ProposedMeterage { get; init; }
    public string ProposedDescription { get; init; } = string.Empty;

    [JsonIgnore]
    public Occurrence? Target { get; init; }

    [JsonIgnore]
    public bool CanApply => Status == InspectionCorrectionResolutionStatus.Ready && Target is not null;
}

public sealed class InspectionCorrectionPlan
{
    public Guid ReportId { get; init; }
    public Report? Report { get; init; }
    public List<InspectionCorrectionResolution> Items { get; init; } = [];
    public int ApplicableCount => Items.Count(x => x.CanApply);
    public int InvalidCount => Items.Count(x =>
        x.Status is InspectionCorrectionResolutionStatus.Invalid or InspectionCorrectionResolutionStatus.NotFound);
}

public sealed record InspectionCorrectionSnapshot(
    Occurrence Occurrence,
    decimal? Meterage,
    string? ManualDescriptionOverride,
    DateTime UpdatedAt);

public sealed class InspectionCorrectionService
{
    public const int MaxCorrectionsPerPackage = 1000;
    public const int MaxDescriptionLength = 4000;

    public InspectionCorrectionPlan Prepare(Project project, InspectionCorrectionPackage package)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(package);

        var corrections = package.Corrections ?? [];
        if (corrections.Count > MaxCorrectionsPerPackage)
            throw new InvalidDataException(
                $"O pacote possui {corrections.Count} correções; o limite é {MaxCorrectionsPerPackage}.");

        var report = project.Reports.SingleOrDefault(x => x.Id == package.ReportId);
        var duplicateKeys = corrections
            .GroupBy(x => (x.SectionId, x.OccurrenceId))
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet();

        var items = corrections.Select(correction =>
            Resolve(project, report, correction, duplicateKeys.Contains((correction.SectionId, correction.OccurrenceId))))
            .ToList();

        return new InspectionCorrectionPlan
        {
            ReportId = package.ReportId,
            Report = report,
            Items = items
        };
    }

    public IReadOnlyList<InspectionCorrectionSnapshot> Apply(InspectionCorrectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var snapshots = new List<InspectionCorrectionSnapshot>();
        var now = DateTime.UtcNow;

        foreach (var item in plan.Items.Where(x => x.CanApply))
        {
            var occurrence = item.Target!;
            snapshots.Add(new InspectionCorrectionSnapshot(
                occurrence, occurrence.Meterage, occurrence.ManualDescriptionOverride, occurrence.UpdatedAt));

            if (item.Correction.HasMeterage)
                occurrence.Meterage = item.Correction.Meterage;
            if (item.Correction.HasDescription)
                occurrence.ManualDescriptionOverride = NormalizeDescription(
                    item.Correction.Description, occurrence.GeneratedDescription);
            occurrence.UpdatedAt = now;
        }

        return snapshots;
    }

    public void Rollback(IEnumerable<InspectionCorrectionSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        foreach (var snapshot in snapshots)
        {
            snapshot.Occurrence.Meterage = snapshot.Meterage;
            snapshot.Occurrence.ManualDescriptionOverride = snapshot.ManualDescriptionOverride;
            snapshot.Occurrence.UpdatedAt = snapshot.UpdatedAt;
        }
    }

    private static InspectionCorrectionResolution Resolve(Project project, Report? report,
        InspectionCorrection correction, bool duplicated)
    {
        if (correction.SectionId == Guid.Empty || correction.OccurrenceId == Guid.Empty)
            return Failure(correction, InspectionCorrectionResolutionStatus.Invalid,
                "SectionId e OccurrenceId são obrigatórios.");
        if (report is null)
            return Failure(correction, InspectionCorrectionResolutionStatus.NotFound,
                "O relatório informado não pertence ao projeto aberto.");
        if (duplicated)
            return Failure(correction, InspectionCorrectionResolutionStatus.Invalid,
                "A ocorrência aparece mais de uma vez no mesmo pacote.");
        if (!correction.HasMeterage && !correction.HasDescription)
            return Failure(correction, InspectionCorrectionResolutionStatus.Invalid,
                "Nenhum campo corrigível foi enviado.");
        if (correction.HasMeterage && correction.Meterage < 0)
            return Failure(correction, InspectionCorrectionResolutionStatus.Invalid,
                "A metragem não pode ser negativa.");
        if (correction.HasDescription && correction.Description?.Length > MaxDescriptionLength)
            return Failure(correction, InspectionCorrectionResolutionStatus.Invalid,
                $"O texto excede {MaxDescriptionLength} caracteres.");

        var sectionEntry = project.Locations
            .SelectMany(location => location.Sections.Select(section => new { Location = location, Section = section }))
            .SingleOrDefault(x => x.Section.Id == correction.SectionId);
        if (sectionEntry is null)
            return Failure(correction, InspectionCorrectionResolutionStatus.NotFound,
                "O trecho não foi encontrado no projeto aberto.");
        if (!report.SectionLinks.Any(x => x.SectionId == correction.SectionId))
            return Failure(correction, InspectionCorrectionResolutionStatus.NotFound,
                "O trecho não pertence ao relatório informado.",
                sectionEntry.Location.Name, sectionEntry.Section.Name);

        var occurrence = sectionEntry.Section.Occurrences
            .SingleOrDefault(x => x.Id == correction.OccurrenceId);
        if (occurrence is null)
            return Failure(correction, InspectionCorrectionResolutionStatus.NotFound,
                "A ocorrência não foi encontrada no trecho informado.",
                sectionEntry.Location.Name, sectionEntry.Section.Name);

        var proposedDescription = correction.HasDescription
            ? EffectiveDescription(correction.Description, occurrence.GeneratedDescription)
            : occurrence.EffectiveDescription;
        var proposedOverride = correction.HasDescription
            ? NormalizeDescription(correction.Description, occurrence.GeneratedDescription)
            : occurrence.ManualDescriptionOverride;
        var changed = correction.HasMeterage && occurrence.Meterage != correction.Meterage ||
                      correction.HasDescription &&
                      !string.Equals(occurrence.ManualDescriptionOverride, proposedOverride,
                          StringComparison.Ordinal);

        return new InspectionCorrectionResolution
        {
            Correction = correction,
            Status = changed ? InspectionCorrectionResolutionStatus.Ready :
                InspectionCorrectionResolutionStatus.Unchanged,
            Message = changed ? "Pronta para aplicar." : "Os valores já estão atualizados.",
            LocationName = sectionEntry.Location.Name,
            SectionName = BuildSectionName(sectionEntry.Section),
            CurrentMeterage = occurrence.Meterage,
            CurrentDescription = occurrence.EffectiveDescription,
            ProposedMeterage = correction.HasMeterage ? correction.Meterage : occurrence.Meterage,
            ProposedDescription = proposedDescription,
            Target = occurrence
        };
    }

    private static InspectionCorrectionResolution Failure(InspectionCorrection correction,
        InspectionCorrectionResolutionStatus status, string message,
        string locationName = "", string sectionName = "") => new()
    {
        Correction = correction,
        Status = status,
        Message = message,
        LocationName = locationName,
        SectionName = sectionName,
        ProposedMeterage = correction.Meterage,
        ProposedDescription = correction.Description?.Trim() ?? string.Empty
    };

    private static string BuildSectionName(InspectionSection section)
    {
        if (!string.IsNullOrWhiteSpace(section.StartPoint) || !string.IsNullOrWhiteSpace(section.EndPoint))
            return $"{section.StartPoint ?? "?"} - {section.EndPoint ?? "?"}";
        return section.Name;
    }

    private static string? NormalizeDescription(string? description, string generatedDescription)
    {
        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return string.Equals(normalized, generatedDescription?.Trim(), StringComparison.Ordinal)
            ? null : normalized;
    }

    private static string EffectiveDescription(string? description, string generatedDescription)
    {
        var normalized = NormalizeDescription(description, generatedDescription);
        return string.IsNullOrWhiteSpace(normalized) ? generatedDescription : normalized;
    }
}
