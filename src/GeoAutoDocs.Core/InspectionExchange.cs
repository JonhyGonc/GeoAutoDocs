namespace GeoAutoDocs.Core;

public static class InspectionExchangeSchema
{
    public const string Name = "GeoAutoDocs.Inspection";
    public const int CurrentVersion = 1;
}

public sealed class InspectionExchangeDocument
{
    public string Schema { get; set; } = InspectionExchangeSchema.Name;
    public int SchemaVersion { get; set; } = InspectionExchangeSchema.CurrentVersion;
    public DateTime GeneratedAtUtc { get; set; }
    public InspectionExchangeProject Project { get; set; } = new();
    public InspectionExchangeReport Report { get; set; } = new();
    public List<InspectionExchangeLocation> Locations { get; set; } = [];
    public List<InspectionExchangeSection> Sections { get; set; } = [];
}

public sealed class InspectionExchangeProject
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class InspectionExchangeReport
{
    public Guid ReportId { get; set; }
    public string ReportCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Contract { get; set; } = string.Empty;
    public DateTime? ReportDate { get; set; }
    public decimal? TotalInvestigatedMeters { get; set; }
}

public sealed class InspectionExchangeLocation
{
    public Guid LocationId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
}

public sealed class InspectionExchangeSection
{
    public Guid SectionId { get; set; }
    public Guid LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? Diameter { get; set; }
    public string? StartPoint { get; set; }
    public string? EndPoint { get; set; }
    public string? Reference { get; set; }
    public decimal? InvestigatedMeters { get; set; }
    public int Order { get; set; }
    public List<InspectionExchangeVideoReference> VideoReferences { get; set; } = [];
    public List<InspectionExchangeOccurrence> Occurrences { get; set; } = [];
}

public sealed class InspectionExchangeVideoReference
{
    public Guid VideoReferenceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? Description { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public sealed class InspectionExchangeOccurrence
{
    public Guid OccurrenceId { get; set; }
    public int Order { get; set; }
    public string LocationMode { get; set; } = string.Empty;
    public decimal? Meterage { get; set; }
    public decimal? EndMeterage { get; set; }
    public List<decimal> MeteragePoints { get; set; } = [];
    public string? ReferencePoint { get; set; }
    public string? Position { get; set; }
    public string? Notes { get; set; }
    public string GeneratedDescription { get; set; } = string.Empty;
    public string? ManualDescriptionOverride { get; set; }
    public string EffectiveDescription { get; set; } = string.Empty;
    public bool InterruptedRecording { get; set; }
    public bool IsSevere { get; set; }
    public Guid? VideoReferenceId { get; set; }
    public TimeSpan? VideoTimestamp { get; set; }
    public List<InspectionExchangeAnomaly> Anomalies { get; set; } = [];
    public List<InspectionExchangeEvidence> Evidence { get; set; } = [];
}

public sealed class InspectionExchangeAnomaly
{
    public Guid AnomalyTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Orientation { get; set; } = string.Empty;
    public decimal? ApproximateExtension { get; set; }
    public string? Dimension { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string InfiltrationCondition { get; set; } = string.Empty;
    public decimal? EstimatedPercentage { get; set; }
    public string? Notes { get; set; }
    public string? CustomText { get; set; }
    public int Order { get; set; }
}

public sealed class InspectionExchangeEvidence
{
    public Guid EvidenceId { get; set; }
    public string Type { get; set; } = string.Empty;
    public Guid? CaptureId { get; set; }
    public Guid? VideoReferenceId { get; set; }
    public TimeSpan? VideoTimestamp { get; set; }
    public string? Caption { get; set; }
    public bool IsPrimary { get; set; }
    public int Order { get; set; }
}

public enum InspectionTransportStatus
{
    Success,
    DestinationUnavailable,
    Timeout,
    IncompatibleVersion,
    InvalidPayload,
    CommunicationError,
    Cancelled
}

public sealed record InspectionTransportResult(
    InspectionTransportStatus Status,
    string Message,
    string? RemoteApplication = null,
    string? RemoteVersion = null,
    int? ProtocolVersion = null,
    int? InspectionSchemaVersion = null,
    string? Destination = null)
{
    public bool Success => Status == InspectionTransportStatus.Success;
}

public interface IInspectionTransport
{
    Task<InspectionTransportResult> SendAsync(
        InspectionExchangeDocument document,
        CancellationToken cancellationToken = default);
}

public interface IInspectionAvailabilityProbe
{
    Task<InspectionTransportResult> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default);
}
