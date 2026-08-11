using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace GeoAutoDocs.Core;

public enum ProjectStorageKind { Local, Shared }

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? ProjectPath { get; set; }
    public ProjectStorageKind StorageKind { get; set; }
    public string? SharedProjectPath { get; set; }
    public long ProjectDataVersion { get; set; }

    public string DefaultClient { get; set; } = string.Empty;
    public string DefaultCnpj { get; set; } = string.Empty;
    public string DefaultArea { get; set; } = string.Empty;
    public string DefaultContract { get; set; } = string.Empty;
    public string DefaultPurchaseOrder { get; set; } = string.Empty;
    public string? DefaultLogoPath { get; set; }

    public List<Report> Reports { get; set; } = [];
    public List<Location> Locations { get; set; } = [];
    public List<Capture> Captures { get; set; } = [];

    [NotMapped]
    [JsonPropertyName("PendingCaptures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Capture>? LegacyPendingCaptures
    {
        get => null;
        set
        {
            if (value is { Count: > 0 } && Captures.Count == 0) Captures = value;
        }
    }

    public Report CreateReport(string? name = null)
    {
        var order = Reports.Count == 0 ? 0 : Reports.Max(x => x.Order) + 1;
        var report = new Report
        {
            ProjectId = Id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Report {order + 1:000}" : name.Trim(),
            Order = order,
            Client = DefaultClient,
            Cnpj = DefaultCnpj,
            Area = DefaultArea,
            Contract = DefaultContract,
            PurchaseOrder = DefaultPurchaseOrder,
            LogoPath = DefaultLogoPath
        };
        Reports.Add(report);
        return report;
    }
}

public sealed class Report
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Client { get; set; } = string.Empty;
    public string Cnpj { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Contract { get; set; } = string.Empty;
    public string PurchaseOrder { get; set; } = string.Empty;
    public string ReportCode { get; set; } = string.Empty;
    public DateTime? ReportDate { get; set; }
    public string LocationDescription { get; set; } = string.Empty;
    public decimal? TotalInvestigatedMeters { get; set; }
    public string? LogoPath { get; set; }
    public string? LocationImagePath { get; set; }
    public string? TemplatePath { get; set; }
    public string? GeneratedReportPath { get; set; }
    public EquipmentSelection Equipments { get; set; }
    public StandardDiameterSelection Diameters { get; set; }
    public string CustomDiameters { get; set; } = string.Empty;
    public bool IncludeInspectionOpening { get; set; }
    public bool IncludeGpr { get; set; }
    public List<CustomSection> CustomSections { get; set; } = [];
    public List<ReportSection> SectionLinks { get; set; } = [];

    [NotMapped]
    public IReadOnlyList<InspectionSection> Sections => SectionLinks
        .OrderBy(x => x.Order).Select(x => x.Section).ToList();

    public void SelectSection(InspectionSection section)
    {
        if (SectionLinks.Any(x => x.SectionId == section.Id)) return;
        SectionLinks.Add(new ReportSection
        {
            ReportId = Id,
            SectionId = section.Id,
            Section = section,
            Order = SectionLinks.Count
        });
    }
}

public sealed class ReportSection
{
    public Guid ReportId { get; set; }
    public Guid SectionId { get; set; }
    public int Order { get; set; }
    public InspectionSection Section { get; set; } = null!;
}

[Flags]
public enum EquipmentSelection { None = 0, Endoscope = 1, Bug = 2, Hunter = 4, Spider = 8, Tracker = 16 }

[Flags]
public enum StandardDiameterSelection
{
    None = 0, Mm300 = 1, Mm400 = 2, Mm500 = 4, Mm600 = 8, Mm800 = 16, Mm1000 = 32, Mm1500 = 64
}

public enum LocationType { Street, Manhole, Site, Area, Other }

public sealed class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public LocationType Type { get; set; } = LocationType.Other;
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public List<InspectionSection> Sections { get; set; } = [];
}

public sealed class InspectionSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? Diameter { get; set; }
    public string? StartPoint { get; set; }
    public string? EndPoint { get; set; }
    public string? Reference { get; set; }
    public decimal? InvestigatedMeters { get; set; }
    public int Order { get; set; }
    public List<VideoReference> VideoReferences { get; set; } = [];
    public List<Occurrence> Occurrences { get; set; } = [];
}

public enum MediaReferenceStatus { LocalOnly, SharedUnc }

public static class MediaPathClassifier
{
    public static MediaReferenceStatus Classify(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith(@"\\", StringComparison.Ordinal)
            ? MediaReferenceStatus.SharedUnc
            : MediaReferenceStatus.LocalOnly;
}

public sealed class VideoReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionSectionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? Description { get; set; }
    public DateTime? CreatedAt { get; set; }
    [NotMapped] public MediaReferenceStatus Status => MediaPathClassifier.Classify(FilePath);
    [NotMapped] public bool IsAvailable => File.Exists(FilePath);
}

public enum OccurrenceLocationMode
{
    Point = 0, Range = 1, EntireSection = 2, NotApplicable = 3,
    SpecificMeterage = Point, Interval = Range
}

public enum AnomalyPosition
{
    NotReported, UpperGenerator, LowerGenerator, LeftSide, RightSide, Circumferential, NotApplicable
}

public sealed class Occurrence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionSectionId { get; set; }
    public Guid? VideoReferenceId { get; set; }
    public OccurrenceLocationMode LocationMode { get; set; }
    public decimal? Meterage { get; set; }
    public decimal? EndMeterage { get; set; }
    public TimeSpan? VideoTimestamp { get; set; }
    public string? ReferencePoint { get; set; }
    public string? Position { get; set; }
    public string? Notes { get; set; }
    public string? CustomGeneratedText { get; set; }
    public string GeneratedDescription { get; set; } = string.Empty;
    public string? ManualDescriptionOverride { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Order { get; set; }
    public List<OccurrenceAnomaly> Anomalies { get; set; } = [];
    public List<Evidence> Evidence { get; set; } = [];

    [NotMapped]
    public string EffectiveDescription => string.IsNullOrWhiteSpace(ManualDescriptionOverride)
        ? GeneratedDescription : ManualDescriptionOverride.Trim();
}

public sealed class AnomalyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
    public bool RequiresPosition { get; set; }
    public bool AllowsCustomText { get; set; }
}

public sealed class OccurrenceAnomaly
{
    public Guid OccurrenceId { get; set; }
    public Guid AnomalyTypeId { get; set; }
    public AnomalyPosition Position { get; set; } = AnomalyPosition.NotReported;
    public string? CustomText { get; set; }
    public int Order { get; set; }
    public AnomalyDefinition AnomalyType { get; set; } = null!;
}

public enum EvidenceSourceType { VideoCapture, ExternalImage }

public sealed class Evidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OccurrenceId { get; set; }
    public Guid? CaptureId { get; set; }
    public EvidenceSourceType SourceType { get; set; }
    public Guid? VideoReferenceId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public TimeSpan? VideoTimestamp { get; set; }
    public int Order { get; set; }
    public string? Caption { get; set; }
    public bool ManagedByGeoAutoDocs { get; set; }
    public string? SharedFilePath { get; set; }
}

public enum CaptureStatus { Pending, Assigned }

public sealed class Capture
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid SectionId { get; set; }
    public Guid VideoReferenceId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? VideoFilePath { get; set; }
    public TimeSpan VideoTimestamp { get; set; }
    public DateTime CreatedAt { get; set; }
    public CaptureStatus Status { get; set; } = CaptureStatus.Pending;
    public int Order { get; set; }
    public bool ManagedByGeoAutoDocs { get; set; } = true;
    public string? SharedFilePath { get; set; }
}

public sealed class CustomSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ReportId { get; set; }
    public string? Title { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Order { get; set; }
    public List<CustomSectionImage> Images { get; set; } = [];
}

public sealed class CustomSectionImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomSectionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int Order { get; set; }
}
