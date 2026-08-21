using System.Globalization;
using System.Text.RegularExpressions;

namespace GeoAutoDocs.Core;

public static partial class AnomalyTemplate
{
    public static readonly IReadOnlyList<string> AllowedVariables =
    [
        "nome", "posicao", "orientacao", "extensao", "dimensao", "condicao",
        "condicaoInfiltracao", "percentual", "textoComplementar"
    ];

    public static void Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return;
        var matches = VariableRegex().Matches(template);
        var stripped = VariableRegex().Replace(template, string.Empty);
        if (stripped.Contains('{') || stripped.Contains('}'))
            throw new InvalidOperationException("O modelo contém chaves inválidas.");
        var invalid = matches.Select(x => x.Groups[1].Value)
            .FirstOrDefault(x => !AllowedVariables.Contains(x, StringComparer.OrdinalIgnoreCase));
        if (invalid is not null)
            throw new InvalidOperationException($"A variável {{{invalid}}} não é permitida.");
    }

    public static string Render(string template, AnomalyDefinition definition, OccurrenceAnomaly anomaly)
    {
        Validate(template);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nome"] = anomaly.DisplayName,
            ["posicao"] = OccurrenceRules.FormatPosition(anomaly.Position),
            ["orientacao"] = OccurrenceRules.FormatOrientation(anomaly.Orientation),
            ["extensao"] = anomaly.ApproximateExtension?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty,
            ["dimensao"] = anomaly.Dimension?.Trim() ?? string.Empty,
            ["condicao"] = OccurrenceRules.FormatCondition(anomaly.Condition),
            ["condicaoInfiltracao"] = OccurrenceRules.FormatInfiltration(anomaly.InfiltrationCondition),
            ["percentual"] = anomaly.EstimatedPercentage?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty,
            ["textoComplementar"] = anomaly.Notes?.Trim() ?? string.Empty
        };
        return Regex.Replace(VariableRegex().Replace(template, m => values[m.Groups[1].Value]), @"\s+([,.;:])", "$1").Trim();
    }

    public static string Preview(AnomalyDefinition definition) => Render(definition.DescriptionTemplate, definition,
        new OccurrenceAnomaly
        {
            AnomalyType = definition, AnomalyName = definition.Name, Position = AnomalyPosition.UpperGenerator,
            Orientation = AnomalyOrientation.Longitudinal, ApproximateExtension = 1.5m, Dimension = "20 mm",
            Condition = AnomalyCondition.Moderate, InfiltrationCondition = InfiltrationCondition.Dripping,
            EstimatedPercentage = 25, Notes = "observação complementar"
        });

    [GeneratedRegex(@"\{([A-Za-zÀ-ÿ]+)\}")]
    private static partial Regex VariableRegex();
}
