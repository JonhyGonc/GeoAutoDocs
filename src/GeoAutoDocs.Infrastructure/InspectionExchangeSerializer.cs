using System.Text.Json;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed class InspectionExchangeSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string Serialize(InspectionExchangeDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public InspectionExchangeDocument Deserialize(string json)
    {
        InspectionExchangeDocument document;
        try
        {
            document = JsonSerializer.Deserialize<InspectionExchangeDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("O documento de intercâmbio está vazio.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("O JSON de intercâmbio é inválido.", ex);
        }
        Validate(document);
        return document;
    }

    public void Validate(InspectionExchangeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.Schema, InspectionExchangeSchema.Name, StringComparison.Ordinal))
            throw new InvalidDataException($"Schema de intercâmbio não suportado: '{document.Schema}'.");
        if (document.SchemaVersion != InspectionExchangeSchema.CurrentVersion)
            throw new NotSupportedException($"Versão {document.SchemaVersion} do intercâmbio não suportada.");
        if (document.Project is null || document.Report is null || document.Locations is null || document.Sections is null)
            throw new InvalidDataException("O documento de intercâmbio está incompleto.");
        if (document.Project.ProjectId == Guid.Empty)
            throw new InvalidDataException("O identificador do projeto é obrigatório.");
        if (document.Report.ReportId == Guid.Empty)
            throw new InvalidDataException("O identificador do relatório é obrigatório.");
        var locationIds = document.Locations.Select(x => x.LocationId).ToHashSet();
        if (locationIds.Contains(Guid.Empty) || locationIds.Count != document.Locations.Count)
            throw new InvalidDataException("O documento contém locais sem identidade estável ou duplicados.");
        if (document.Sections.Any(x => x.SectionId == Guid.Empty || !locationIds.Contains(x.LocationId)))
            throw new InvalidDataException("O documento contém trecho sem local válido.");
        if (document.Sections.Select(x => x.SectionId).Distinct().Count() != document.Sections.Count)
            throw new InvalidDataException("O documento contém trechos duplicados.");
        var occurrences = document.Sections.SelectMany(x => x.Occurrences ?? []).ToList();
        if (occurrences.Any(x => x.OccurrenceId == Guid.Empty) ||
            occurrences.Select(x => x.OccurrenceId).Distinct().Count() != occurrences.Count)
            throw new InvalidDataException("O documento contém ocorrências sem identidade estável ou duplicadas.");
    }
}
