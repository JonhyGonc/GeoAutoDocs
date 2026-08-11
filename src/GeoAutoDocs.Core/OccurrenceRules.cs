using System.Globalization;

namespace GeoAutoDocs.Core;

public static class AnomalyCatalog
{
    public static readonly Guid OtherId = Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf43300c");

    public static IReadOnlyList<AnomalyDefinition> Defaults { get; } =
    [
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433001", "Fissura", 0),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433002", "Trinca", 1),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433003", "Infiltração", 2),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433004", "Abaulamento", 3, true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433005", "Junção de rede", 4, true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433006", "Obstrução", 5),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433007", "Assoreamento", 6),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433008", "Colapso", 7),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433009", "Deformação", 8),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300a", "Contribuição", 9, true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300b", "Caixa de passagem", 10),
        New(OtherId.ToString(), "Outro", 11, allowsCustomText: true)
    ];

    private static AnomalyDefinition New(string id, string name, int order,
        bool requiresPosition = false, bool allowsCustomText = false) => new()
    {
        Id = Guid.Parse(id), Name = name, Order = order, IsActive = true,
        RequiresPosition = requiresPosition, AllowsCustomText = allowsCustomText
    };
}

public static class OccurrenceRules
{
    public static void Validate(Occurrence occurrence)
    {
        if (occurrence.InspectionSectionId == Guid.Empty)
            throw new InvalidOperationException("O trecho da ocorrência é obrigatório.");

        switch (occurrence.LocationMode)
        {
            case OccurrenceLocationMode.Point:
                if (occurrence.Meterage is null || occurrence.Meterage < 0)
                    throw new InvalidOperationException("Informe uma metragem pontual válida.");
                break;
            case OccurrenceLocationMode.Range:
                if (occurrence.Meterage is null || occurrence.EndMeterage is null || occurrence.Meterage < 0)
                    throw new InvalidOperationException("Informe o início e o fim do intervalo.");
                if (occurrence.EndMeterage < occurrence.Meterage)
                    throw new InvalidOperationException("A metragem final deve ser maior ou igual à inicial.");
                break;
            case OccurrenceLocationMode.EntireSection:
                break;
            default:
                throw new InvalidOperationException("Selecione a localização da ocorrência.");
        }

        if (occurrence.Anomalies.Count == 0)
            throw new InvalidOperationException("Selecione pelo menos uma anomalia.");
        if (occurrence.Anomalies.Select(x => x.AnomalyTypeId).Distinct().Count() != occurrence.Anomalies.Count)
            throw new InvalidOperationException("A mesma anomalia foi selecionada mais de uma vez.");

        foreach (var item in occurrence.Anomalies)
        {
            if (item.AnomalyType is null) throw new InvalidOperationException("Tipo de anomalia inválido.");
            if (item.AnomalyType.AllowsCustomText && string.IsNullOrWhiteSpace(item.CustomText))
                throw new InvalidOperationException($"Descreva a anomalia ‘{item.AnomalyType.Name}’.");
            if (item.AnomalyType.RequiresPosition && item.Position == AnomalyPosition.NotReported)
                throw new InvalidOperationException($"Informe a posição de ‘{item.AnomalyType.Name}’.");
        }
    }

    public static string FormatLocation(Occurrence occurrence) => occurrence.LocationMode switch
    {
        OccurrenceLocationMode.Point when occurrence.Meterage is not null => $"{FormatMeters(occurrence.Meterage.Value)} m",
        OccurrenceLocationMode.Range when occurrence.Meterage is not null && occurrence.EndMeterage is not null =>
            $"{FormatMeters(occurrence.Meterage.Value)} m a {FormatMeters(occurrence.EndMeterage.Value)} m",
        OccurrenceLocationMode.EntireSection => "Todo o trecho",
        _ => "Localização não informada"
    };

    public static string GenerateDescription(Occurrence occurrence)
    {
        var anomalyNames = occurrence.Anomalies.OrderBy(x => x.Order).Select(x =>
            x.AnomalyType.AllowsCustomText && !string.IsNullOrWhiteSpace(x.CustomText)
                ? x.CustomText!.Trim() : x.AnomalyType.Name.Trim()).ToList();
        var anomalies = Join(anomalyNames);
        var location = occurrence.LocationMode switch
        {
            OccurrenceLocationMode.Point => $"Na metragem de {FormatMeters(occurrence.Meterage!.Value)} m",
            OccurrenceLocationMode.Range => $"Entre {FormatMeters(occurrence.Meterage!.Value)} m e {FormatMeters(occurrence.EndMeterage!.Value)} m",
            OccurrenceLocationMode.EntireSection => "Ao longo de todo o trecho",
            _ => "No trecho"
        };
        return $"{location}, observa-se a presença de {anomalies}.";
    }

    public static bool IsComplete(Occurrence occurrence) => occurrence.Evidence.Count > 0 &&
        !string.IsNullOrWhiteSpace(occurrence.EffectiveDescription);

    private static string FormatMeters(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));

    private static string Join(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "anomalia não classificada",
        1 => values[0],
        2 => $"{values[0]} e {values[1]}",
        _ => string.Join(", ", values.Take(values.Count - 1)) + " e " + values[^1]
    };
}
