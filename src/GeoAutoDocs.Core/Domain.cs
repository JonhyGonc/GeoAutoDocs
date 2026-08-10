namespace GeoAutoDocs.Core;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? ProjectPath { get; set; }
    public ReportConfiguration ReportConfiguration { get; set; } = new();
    public List<Location> Locations { get; set; } = [];
    public List<PendingCapture> PendingCaptures { get; set; } = [];
    public List<CustomSection> CustomSections { get; set; } = [];
}

public sealed class ReportConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Client { get; set; } = string.Empty;
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
}

[Flags]
public enum EquipmentSelection
{
    None = 0,
    Endoscope = 1,
    Bug = 2,
    Hunter = 4,
    Spider = 8,
    Tracker = 16
}

[Flags]
public enum StandardDiameterSelection
{
    None = 0,
    Mm300 = 1,
    Mm400 = 2,
    Mm500 = 4,
    Mm600 = 8,
    Mm800 = 16,
    Mm1000 = 32,
    Mm1500 = 64
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
    public decimal? Diameter { get; set; }
    public string? Reference { get; set; }
    public decimal? InvestigatedMeters { get; set; }
    public int Order { get; set; }
    public VideoReference? VideoReference { get; set; }
    public List<Occurrence> Occurrences { get; set; } = [];
}

public sealed class VideoReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionSectionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
}

public enum OccurrenceLocationMode { SpecificMeterage, Interval, EntireSection, NotApplicable }

public sealed class Occurrence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionSectionId { get; set; }
    public OccurrenceLocationMode LocationMode { get; set; }
    public decimal? Meterage { get; set; }
    public decimal? EndMeterage { get; set; }
    public TimeSpan? VideoTimestamp { get; set; }
    public string? Position { get; set; }
    public string? Notes { get; set; }
    public string? CustomGeneratedText { get; set; }
    public int Order { get; set; }
    public List<AnomalyDefinition> Anomalies { get; set; } = [];
    public List<Evidence> Evidence { get; set; } = [];
}

public sealed class AnomalyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
    public List<Occurrence> Occurrences { get; set; } = [];
}

public sealed class Evidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OccurrenceId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public TimeSpan? VideoTimestamp { get; set; }
    public int Order { get; set; }
    public string? Caption { get; set; }
}

public sealed class PendingCapture
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? VideoFilePath { get; set; }
    public TimeSpan VideoTimestamp { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CustomSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
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
