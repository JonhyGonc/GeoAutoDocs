using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed class InspectionExchangeService
{
    public const string ManifestNamespace = "urn:geoautodocs:inspection-exchange:v1";
    public const string ManifestElementName = "GeoAutoDocsInspectionManifest";
    public InspectionExchangeSerializer Serializer { get; }

    public InspectionExchangeService(InspectionExchangeSerializer? serializer = null) =>
        Serializer = serializer ?? new InspectionExchangeSerializer();

    public InspectionExchangeDocument Build(Project project, Report report, DateTime? generatedAtUtc = null)
    {
        if (report.ProjectId != project.Id)
            throw new InvalidOperationException("O relatório não pertence ao projeto selecionado.");

        var selected = report.SectionLinks.Select(x => x.SectionId).ToList();
        var selectedSet = selected.ToHashSet();
        var orderedSections = project.Locations
            .OrderBy(location => location.Order)
            .SelectMany(location => location.Sections
                .OrderBy(section => section.Order)
                .Where(section => selectedSet.Contains(section.Id))
                .Select(section => (Location: location, Section: section)))
            .ToList();
        var locationBySection = orderedSections.ToDictionary(x => x.Section.Id);
        var missing = selected.Where(id => !locationBySection.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("Um ou mais trechos selecionados não pertencem ao projeto.");

        var document = new InspectionExchangeDocument
        {
            GeneratedAtUtc = (generatedAtUtc ?? DateTime.UtcNow).ToUniversalTime(),
            Project = new InspectionExchangeProject { ProjectId = project.Id, Name = project.Name },
            Report = new InspectionExchangeReport
            {
                ReportId = report.Id,
                ReportCode = report.ReportCode,
                Name = report.Name,
                Client = report.Client,
                Area = report.Area,
                Contract = report.Contract,
                ReportDate = report.ReportDate,
                TotalInvestigatedMeters = report.TotalInvestigatedMeters
            }
        };

        document.Locations = orderedSections.Select(x => x.Location).DistinctBy(x => x.Id)
            .Select(x => new InspectionExchangeLocation
            {
                LocationId = x.Id, Type = x.Type.ToString(), Name = x.Name, Order = x.Order
            }).ToList();
        document.Sections = orderedSections
            .Select(x => MapSection(x.Location, x.Section))
            .ToList();
        Serializer.Validate(document);
        return document;
    }

    public bool TryReadInspectionManifest(string docxPath, out InspectionExchangeDocument? document,
        out string? error)
    {
        document = null;
        error = null;
        try
        {
            if (!File.Exists(docxPath))
                throw new FileNotFoundException("O DOCX não foi encontrado.", docxPath);
            using var package = WordprocessingDocument.Open(docxPath, false);
            var mainPart = package.MainDocumentPart ?? throw new InvalidDataException("DOCX sem documento principal.");
            var part = FindManifestPart(mainPart);
            if (part is null) return false;
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            var xml = XDocument.Load(stream);
            var root = xml.Root ?? throw new InvalidDataException("Manifesto de inspeção vazio.");
            if (!int.TryParse(root.Attribute("schemaVersion")?.Value, out var version))
                throw new InvalidDataException("Manifesto de inspeção sem versão válida.");
            if (version != InspectionExchangeSchema.CurrentVersion)
                throw new NotSupportedException($"Versão {version} do manifesto de inspeção não suportada.");
            document = Serializer.Deserialize(root.Value);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or
                                       OpenXmlPackageException or IOException or UnauthorizedAccessException or
                                       NotSupportedException)
        {
            error = ex.Message;
            document = null;
            return false;
        }
    }

    internal void WriteInspectionManifest(MainDocumentPart mainPart, InspectionExchangeDocument document)
    {
        var xml = new XDocument(new XElement(XName.Get(ManifestElementName, ManifestNamespace),
            new XAttribute("schema", InspectionExchangeSchema.Name),
            new XAttribute("schemaVersion", InspectionExchangeSchema.CurrentVersion),
            new XCData(Serializer.Serialize(document))));
        var part = FindManifestPart(mainPart) ?? mainPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using var stream = new MemoryStream();
        xml.Save(stream, SaveOptions.DisableFormatting);
        stream.Position = 0;
        part.FeedData(stream);
    }

    private static InspectionExchangeSection MapSection(Location location, InspectionSection section) => new()
    {
        SectionId = section.Id,
        LocationId = location.Id,
        Name = section.Name,
        Description = section.Description,
        Diameter = section.Diameter,
        StartPoint = section.StartPoint,
        EndPoint = section.EndPoint,
        Reference = section.Reference,
        InvestigatedMeters = section.InvestigatedMeters,
        Order = section.Order,
        VideoReferences = section.VideoReferences.OrderBy(x => x.Order).Select(x => new InspectionExchangeVideoReference
        {
            VideoReferenceId = x.Id,
            DisplayName = x.DisplayName,
            FileName = Path.GetFileName(x.FilePath),
            Order = x.Order,
            Description = x.Description,
            CreatedAt = x.CreatedAt
        }).ToList(),
        Occurrences = section.Occurrences.OrderBy(x => x.Order).ThenBy(x => x.CreatedAt).Select(MapOccurrence).ToList()
    };

    private static InspectionExchangeOccurrence MapOccurrence(Occurrence occurrence) => new()
    {
        OccurrenceId = occurrence.Id,
        Order = occurrence.Order,
        LocationMode = occurrence.LocationMode.ToString(),
        Meterage = occurrence.Meterage,
        EndMeterage = occurrence.EndMeterage,
        MeteragePoints = occurrence.MeteragePoints.OrderBy(x => x.Order).Select(x => x.Meterage).ToList(),
        ReferencePoint = occurrence.ReferencePoint,
        Position = occurrence.Position,
        Notes = occurrence.Notes,
        GeneratedDescription = occurrence.GeneratedDescription,
        ManualDescriptionOverride = occurrence.ManualDescriptionOverride,
        EffectiveDescription = occurrence.EffectiveDescription,
        InterruptedRecording = occurrence.InterruptedRecording,
        IsSevere = occurrence.IsSevere,
        VideoReferenceId = occurrence.VideoReferenceId,
        VideoTimestamp = occurrence.VideoTimestamp,
        Anomalies = occurrence.Anomalies.OrderBy(x => x.Order).Select(x => new InspectionExchangeAnomaly
        {
            AnomalyTypeId = x.AnomalyTypeId,
            Name = x.DisplayName,
            Position = x.Position.ToString(),
            Orientation = x.Orientation.ToString(),
            ApproximateExtension = x.ApproximateExtension,
            Dimension = x.Dimension,
            Condition = x.Condition.ToString(),
            InfiltrationCondition = x.InfiltrationCondition.ToString(),
            EstimatedPercentage = x.EstimatedPercentage,
            Notes = x.Notes,
            CustomText = x.CustomText,
            Order = x.Order
        }).ToList(),
        Evidence = occurrence.Evidence.OrderBy(x => x.Order).Select(x => new InspectionExchangeEvidence
        {
            EvidenceId = x.Id,
            Type = x.SourceType.ToString(),
            CaptureId = x.CaptureId,
            VideoReferenceId = x.VideoReferenceId,
            VideoTimestamp = x.VideoTimestamp,
            Caption = x.Caption,
            IsPrimary = x.IsPrimary,
            Order = x.Order
        }).ToList()
    };

    private static CustomXmlPart? FindManifestPart(MainDocumentPart mainPart)
    {
        foreach (var part in mainPart.CustomXmlParts)
        {
            try
            {
                using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
                var xml = XDocument.Load(stream);
                if (xml.Root?.Name == XName.Get(ManifestElementName, ManifestNamespace)) return part;
            }
            catch (Exception) when (part is not null) { }
        }
        return null;
    }
}

public sealed class InspectionExchangeExportException(string docxPath, Exception innerException)
    : IOException($"O DOCX foi gerado, mas o arquivo de intercâmbio não pôde ser salvo: {innerException.Message}", innerException)
{
    public string DocxPath { get; } = docxPath;
}
