using System.Globalization;

namespace GeoAutoDocs.Core;

public static class AnomalyCatalog
{
    public static readonly Guid OtherId = Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf43300c");
    public static readonly Guid SedimentAccumulationId = Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf433012");
    public static IReadOnlySet<Guid> PrimaryIds { get; } = new HashSet<Guid>
    {
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf433001"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf433002"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf433003"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf433006"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf43300a"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf43300f"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf433004"),
        Guid.Parse("9f2f34e7-07c1-4c29-a25c-4d62cf43300b")
    };

    public static IReadOnlyList<AnomalyDefinition> Defaults { get; } =
    [
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433001", "Fissura", 0, orientation: true, extension: true, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433002", "Trinca", 1, orientation: true, extension: true, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433003", "Infiltração", 2, extension: true, condition: true, infiltration: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433004", "Abaulamento", 3, extension: true, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433009", "Deformação", 4, extension: true, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433006", "Obstrução", 5, extension: true, dimension: true, condition: true, percentage: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433007", "Assoreamento", 6, extension: true, condition: true, percentage: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433008", "Colapso", 7, extension: true, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433005", "Junção de rede", 8, dimension: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300a", "Contribuição", 9),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300b", "Caixa de passagem", 10),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300d", "Raiz", 11, extension: true, dimension: true, condition: true, percentage: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300e", "Material depositado", 12, extension: true, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf43300f", "Deslocamento das juntas", 13, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433010", "Junta aberta", 14, dimension: true, condition: true),
        New("9f2f34e7-07c1-4c29-a25c-4d62cf433011", "Rompimento", 15, orientation: true, extension: true, dimension: true, condition: true),
        New(OtherId.ToString(), "Outro", 16, allowsCustomText: true, orientation: true, extension: true, dimension: true, condition: true),
        New(SedimentAccumulationId.ToString(), "Acúmulo de sedimentos", 17, position: true)
    ];

    private static AnomalyDefinition New(string id, string name, int order,
        bool requiresPosition = false, bool allowsCustomText = false, bool position = true,
        bool orientation = false, bool extension = false, bool dimension = false,
        bool condition = false, bool infiltration = false, bool percentage = false) => new()
    {
        Id = Guid.Parse(id), Name = name, Order = order, IsActive = true,
        RequiresPosition = requiresPosition, AllowsCustomText = allowsCustomText,
        SupportsPosition = position, SupportsOrientation = orientation, SupportsExtension = extension,
        SupportsDimension = dimension, SupportsCondition = condition,
        SupportsInfiltrationCondition = infiltration, SupportsPercentage = percentage
    };
}

public static class OccurrenceRules
{
    public static IReadOnlyList<AnomalyPosition> SelectablePositions { get; } =
    [
        AnomalyPosition.NotApplicable,
        AnomalyPosition.UpperGenerator,
        AnomalyPosition.LowerGenerator,
        AnomalyPosition.LeftSide,
        AnomalyPosition.RightSide,
        AnomalyPosition.Circumferential
    ];

    public static void Validate(Occurrence occurrence)
    {
        if (occurrence.InspectionSectionId == Guid.Empty)
            throw new InvalidOperationException("O trecho da anomalia é obrigatório.");

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
            case OccurrenceLocationMode.MultiPoint:
                if (occurrence.MeteragePoints is null || occurrence.MeteragePoints.Count < 2)
                    throw new InvalidOperationException("Informe pelo menos duas metragens distintas.");
                if (occurrence.MeteragePoints.Any(x => x.Meterage < 0))
                    throw new InvalidOperationException("As metragens dos múltiplos pontos não podem ser negativas.");
                if (occurrence.MeteragePoints.Select(x => x.Meterage).Distinct().Count() != occurrence.MeteragePoints.Count)
                    throw new InvalidOperationException("A mesma metragem foi adicionada mais de uma vez.");
                if (!occurrence.MeteragePoints.Select(x => x.Meterage)
                        .SequenceEqual(occurrence.MeteragePoints.OrderBy(x => x.Meterage).Select(x => x.Meterage)))
                    throw new InvalidOperationException("As metragens dos múltiplos pontos devem estar em ordem crescente.");
                if (occurrence.EndMeterage is not null)
                    throw new InvalidOperationException("Múltiplos pontos não possuem metragem final.");
                break;
            case OccurrenceLocationMode.From:
                if (occurrence.Meterage is null || occurrence.Meterage < 0)
                    throw new InvalidOperationException("Informe uma metragem inicial válida.");
                if (occurrence.EndMeterage is not null)
                    throw new InvalidOperationException("A localização ‘A partir de’ não possui metragem final.");
                break;
            case OccurrenceLocationMode.EntireSection:
                if (occurrence.Meterage is not null || occurrence.EndMeterage is not null)
                    throw new InvalidOperationException("Uma ocorrência em todo o trecho não possui metragem.");
                break;
            default:
                throw new InvalidOperationException("Selecione a localização da anomalia.");
        }

        if (occurrence.Anomalies.Count == 0)
            throw new InvalidOperationException("Selecione pelo menos um tipo de anomalia.");
        if (occurrence.Anomalies.Select(x => x.AnomalyTypeId).Distinct().Count() != occurrence.Anomalies.Count)
            throw new InvalidOperationException("A mesma anomalia foi selecionada mais de uma vez.");

        foreach (var item in occurrence.Anomalies)
        {
            if (item.AnomalyType is null) throw new InvalidOperationException("Tipo de anomalia inválido.");
            if (item.AnomalyType.AllowsCustomText && string.IsNullOrWhiteSpace(item.CustomText))
                throw new InvalidOperationException($"Descreva a anomalia ‘{item.AnomalyType.Name}’.");
            if (item.AnomalyType.RequiresPosition && item.Position == AnomalyPosition.NotReported)
                throw new InvalidOperationException($"Informe a posição de ‘{item.AnomalyType.Name}’.");
            if (item.ApproximateExtension is < 0)
                throw new InvalidOperationException($"A extensão de ‘{item.AnomalyType.Name}’ não pode ser negativa.");
            if (item.EstimatedPercentage is < 0 or > 100)
                throw new InvalidOperationException($"O percentual de ‘{item.AnomalyType.Name}’ deve estar entre 0 e 100.");
        }
    }

    public static string FormatLocation(Occurrence occurrence) => occurrence.LocationMode switch
    {
        OccurrenceLocationMode.Point when occurrence.Meterage is not null => $"{FormatMeters(occurrence.Meterage.Value)} m",
        OccurrenceLocationMode.Range when occurrence.Meterage is not null && occurrence.EndMeterage is not null =>
            $"{FormatMeters(occurrence.Meterage.Value)} m a {FormatMeters(occurrence.EndMeterage.Value)} m",
        OccurrenceLocationMode.MultiPoint when occurrence.MeteragePoints.Count >= 2 =>
            $"{FormatMeterageList(occurrence.MeteragePoints.Select(x => x.Meterage).ToList())} m",
        OccurrenceLocationMode.From when occurrence.Meterage is not null =>
            $"A partir de {FormatMeters(occurrence.Meterage.Value)} m",
        OccurrenceLocationMode.EntireSection => "Todo o trecho",
        _ => "Localização não informada"
    };

    public static string GenerateDescription(Occurrence occurrence)
    {
        var ordered = occurrence.Anomalies.OrderBy(x => x.Order).ToList();
        var anomalySummary = FormatAnomalySummary(ordered);
        var location = FormatFilmingReference(occurrence);
        var reference = string.IsNullOrWhiteSpace(occurrence.ReferencePoint)
            ? string.Empty : $", com referência em {occurrence.ReferencePoint.Trim()}";
        var ending = occurrence.InterruptedRecording ? ", interrompendo a gravação." : ".";
        var details = ordered.Select(FormatTechnicalDetails)
            .Where(x => x.Length > 0).ToList();
        var detailText = details.Count == 0 ? string.Empty : " " + string.Join(" ", details);
        return ApplySeverityMarker($"{anomalySummary} {location}{reference}{ending}{detailText}".Trim(), occurrence.IsSevere);
    }

    public static string ApplySeverityMarker(string description, bool isSevere)
    {
        var text = description.Trim();
        if (!isSevere) return text;
        var withoutEnding = text.TrimEnd('.', ';');
        if (withoutEnding.EndsWith("(grave)", StringComparison.CurrentCultureIgnoreCase)) return text;
        var ending = withoutEnding.Length == text.Length ? string.Empty : text[^1].ToString();
        return $"{withoutEnding} (grave){ending}";
    }

    public static string GenerateCaptionSummary(Occurrence occurrence) =>
        $"{FormatAnomalySummary(occurrence.Anomalies.OrderBy(x => x.Order).ToList()).TrimEnd('.', ';')}.";

    public static bool IsComplete(Occurrence occurrence) => GetPendingReasons(occurrence).Count == 0;

    public static IReadOnlyList<string> GetPendingReasons(Occurrence occurrence)
    {
        var pending = new List<string>();
        if (occurrence.Anomalies.Count == 0) pending.Add("Sem anomalia");
        if ((occurrence.LocationMode == OccurrenceLocationMode.Point && occurrence.Meterage is null) ||
            (occurrence.LocationMode == OccurrenceLocationMode.Range &&
             (occurrence.Meterage is null || occurrence.EndMeterage is null)) ||
            (occurrence.LocationMode == OccurrenceLocationMode.MultiPoint && occurrence.MeteragePoints.Count < 2) ||
            (occurrence.LocationMode == OccurrenceLocationMode.From && occurrence.Meterage is null)) pending.Add("Sem metragem");
        if (occurrence.Evidence.Count == 0) pending.Add("Sem evidência");
        if (string.IsNullOrWhiteSpace(occurrence.EffectiveDescription)) pending.Add("Descrição pendente");
        return pending;
    }

    public static string FormatPosition(AnomalyPosition value) => value switch
    {
        AnomalyPosition.UpperGenerator => "Geratriz superior", AnomalyPosition.LowerGenerator => "Geratriz inferior",
        AnomalyPosition.LeftSide => "Lateral esquerda", AnomalyPosition.RightSide => "Lateral direita",
        AnomalyPosition.UpperLeft => "Superior esquerda", AnomalyPosition.UpperRight => "Superior direita",
        AnomalyPosition.LowerLeft => "Inferior esquerda", AnomalyPosition.LowerRight => "Inferior direita",
        AnomalyPosition.Circumferential => "Circunferencial", AnomalyPosition.PipeBottom => "Fundo da tubulação",
        AnomalyPosition.PipeWall => "Parede da tubulação", AnomalyPosition.Joint => "Junta",
        AnomalyPosition.Side => "Lateral",
        AnomalyPosition.NotApplicable => "Não aplicável", _ => "Não informado"
    };

    public static string FormatOrientation(AnomalyOrientation value) => value switch
    {
        AnomalyOrientation.Longitudinal => "Longitudinal", AnomalyOrientation.Transverse => "Transversal",
        AnomalyOrientation.Diagonal => "Diagonal", AnomalyOrientation.Circumferential => "Circunferencial",
        AnomalyOrientation.Irregular => "Irregular", _ => "Não informado"
    };

    public static string FormatCondition(AnomalyCondition value) => value switch
    {
        AnomalyCondition.Light => "Leve", AnomalyCondition.Moderate => "Moderada",
        AnomalyCondition.Accentuated => "Acentuada", _ => "Não classificada"
    };

    public static string FormatInfiltration(InfiltrationCondition value) => value switch
    {
        InfiltrationCondition.MoistureEvidence => "Indício de umidade",
        InfiltrationCondition.Dripping => "Gotejamento",
        InfiltrationCondition.ContinuousFlow => "Fluxo contínuo", _ => "Não informado"
    };

    private static string FormatTechnicalDetails(OccurrenceAnomaly anomaly)
    {
        if (!string.IsNullOrWhiteSpace(anomaly.AnomalyType.DescriptionTemplate))
            return AnomalyTemplate.Render(anomaly.AnomalyType.DescriptionTemplate, anomaly.AnomalyType, anomaly);
        var name = anomaly.AnomalyType.AllowsCustomText && !string.IsNullOrWhiteSpace(anomaly.CustomText)
            ? anomaly.CustomText.Trim() : anomaly.DisplayName;
        var parts = new List<string>();
        if (anomaly.Orientation != AnomalyOrientation.NotReported)
            parts.Add(FormatOrientation(anomaly.Orientation).ToLowerInvariant());
        if (anomaly.ApproximateExtension is not null)
            parts.Add($"extensão aproximada de {FormatMeters(anomaly.ApproximateExtension.Value)} m");
        if (!string.IsNullOrWhiteSpace(anomaly.Dimension)) parts.Add($"dimensão {anomaly.Dimension.Trim()}");
        if (anomaly.EstimatedPercentage is not null)
            parts.Add($"percentual estimado de {FormatMeters(anomaly.EstimatedPercentage.Value)}%");
        if (anomaly.Condition != AnomalyCondition.NotClassified)
            parts.Add(FormatCondition(anomaly.Condition).ToLowerInvariant());
        if (anomaly.InfiltrationCondition != InfiltrationCondition.NotReported)
            parts.Add(FormatInfiltration(anomaly.InfiltrationCondition).ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(anomaly.Notes)) parts.Add(anomaly.Notes.Trim());
        return parts.Count == 0 ? string.Empty : $"{name}: {string.Join(", ", parts)}.";
    }

    private static string FormatPositionGroup(IReadOnlyList<OccurrenceAnomaly> anomalies, bool firstGroup)
    {
        var names = anomalies.Select((item, index) =>
        {
            var name = item.AnomalyType.AllowsCustomText && !string.IsNullOrWhiteSpace(item.CustomText)
                ? item.CustomText!.Trim() : item.DisplayName.Trim();
            return firstGroup && index == 0 ? name : LowercaseInitial(name);
        }).ToList();
        var position = anomalies[0].Position;
        if (position == AnomalyPosition.Side && anomalies.Count == 1 &&
            anomalies[0].DisplayName.Equals("Contribuição", StringComparison.CurrentCultureIgnoreCase))
            return $"{names[0]} lateral";
        return Join(names) + FormatPositionPhrase(position);
    }

    private static string FormatAnomalySummary(IReadOnlyList<OccurrenceAnomaly> ordered)
    {
        var groups = ordered.GroupBy(x => x.Position).OrderBy(x => x.Min(y => y.Order)).ToList();
        return Join(groups.Select((group, index) => FormatPositionGroup(group.ToList(), index == 0)).ToList());
    }

    private static string FormatPositionPhrase(AnomalyPosition position) => position switch
    {
        AnomalyPosition.UpperGenerator => " na geratriz superior",
        AnomalyPosition.LowerGenerator => " na geratriz inferior",
        AnomalyPosition.LeftSide => " na lateral esquerda da tubulação",
        AnomalyPosition.RightSide => " na lateral direita da tubulação",
        AnomalyPosition.Side => " na lateral da tubulação",
        AnomalyPosition.UpperLeft => " na parte superior esquerda da tubulação",
        AnomalyPosition.UpperRight => " na parte superior direita da tubulação",
        AnomalyPosition.LowerLeft => " na parte inferior esquerda da tubulação",
        AnomalyPosition.LowerRight => " na parte inferior direita da tubulação",
        AnomalyPosition.Circumferential => " em toda a circunferência da tubulação",
        AnomalyPosition.PipeBottom => " no fundo da tubulação",
        AnomalyPosition.PipeWall => " na parede da tubulação",
        AnomalyPosition.Joint => " na junta",
        _ => string.Empty
    };

    private static string FormatFilmingReference(Occurrence occurrence) => occurrence.LocationMode switch
    {
        OccurrenceLocationMode.Point when occurrence.Meterage is not null =>
            $"a {FormatMeters(occurrence.Meterage.Value)} {(occurrence.Meterage.Value == 1 ? "metro" : "metros")} do início da filmagem",
        OccurrenceLocationMode.Range when occurrence.Meterage is not null && occurrence.EndMeterage is not null =>
            $"entre {FormatMeters(occurrence.Meterage.Value)} e {FormatMeters(occurrence.EndMeterage.Value)} metros do início da filmagem",
        OccurrenceLocationMode.MultiPoint when occurrence.MeteragePoints.Count >= 2 =>
            $"a {FormatMeterageList(occurrence.MeteragePoints.Select(x => x.Meterage).ToList())} metros do início da filmagem",
        OccurrenceLocationMode.From when occurrence.Meterage is not null =>
            $"a partir de {FormatMeters(occurrence.Meterage.Value)} {(occurrence.Meterage.Value == 1 ? "metro" : "metros")} do início da filmagem",
        OccurrenceLocationMode.EntireSection => "em todo trecho filmado",
        _ => "durante a filmagem"
    };

    private static string FormatMeterageList(IReadOnlyList<decimal> values) =>
        Join(values.Select(FormatMeters).ToList());

    private static string LowercaseInitial(string value) => value.Length == 0
        ? value : char.ToLower(value[0], CultureInfo.GetCultureInfo("pt-BR")) + value[1..];

    private static string FormatMeters(decimal value) =>
        value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));

    private static string Join(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "anomalia não classificada", 1 => values[0], 2 => $"{values[0]} e {values[1]}",
        _ => string.Join(", ", values.Take(values.Count - 1)) + " e " + values[^1]
    };
}
