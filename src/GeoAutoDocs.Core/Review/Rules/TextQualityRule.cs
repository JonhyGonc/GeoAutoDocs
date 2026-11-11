using System.Text.RegularExpressions;

namespace GeoAutoDocs.Core.Review.Rules;

public sealed partial class TextQualityRule : IReportReviewRule
{
    public string Id => "text.quality";
    public string Name => "Qualidade objetiva dos textos";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Options.CheckTextQuality) yield break;

        foreach (var occurrence in context.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.TryGetOccurrenceSection(occurrence.Id, out var section);
            var text = occurrence.EffectiveDescription;

            if (string.IsNullOrWhiteSpace(text))
            {
                yield return Issue(
                    ReviewSeverity.Warning,
                    "Descrição vazia",
                    "A ocorrência não possui descrição para conferência.",
                    section,
                    occurrence);
                continue;
            }

            var trimmed = text.Trim();
            var normalizedPlaceholder = trimmed.Trim('.', ';', ':', '-', '_').Trim();
            if (context.Options.TextPlaceholders.Contains(trimmed) ||
                context.Options.TextPlaceholders.Contains(normalizedPlaceholder))
            {
                yield return Issue(
                    ReviewSeverity.Warning,
                    "Texto provisório",
                    $"A descrição “{trimmed}” parece ser um texto provisório.",
                    section,
                    occurrence);
                continue;
            }

            if (context.Options.MinimumDescriptionLength > 0 &&
                trimmed.Length < context.Options.MinimumDescriptionLength)
                yield return Issue(
                    ReviewSeverity.Info,
                    "Descrição muito curta",
                    $"A descrição possui menos de {context.Options.MinimumDescriptionLength} caracteres.",
                    section,
                    occurrence);

            var repeatedSpace = ExcessiveWhitespace().Match(trimmed);
            if (repeatedSpace.Success)
                yield return Issue(
                    ReviewSeverity.Info,
                    "Espaços consecutivos",
                    "A descrição possui espaços ou quebras consecutivas que podem ser padronizadas.",
                    section,
                    occurrence);

            var repeatedWord = ConsecutiveWord().Match(trimmed);
            if (repeatedWord.Success)
                yield return Issue(
                    ReviewSeverity.Info,
                    "Palavra repetida",
                    $"A descrição repete consecutivamente “{repeatedWord.Groups[1].Value}”.",
                    section,
                    occurrence);
        }
    }

    private ReviewIssue Issue(
        ReviewSeverity severity,
        string title,
        string description,
        ReviewSectionContext? section,
        Occurrence occurrence) => new()
    {
        RuleId = Id,
        Severity = severity,
        Title = title,
        Description = description,
        NavigationArea = ReviewNavigationArea.Inspection,
        LocationId = section?.Location.Id,
        SectionId = section?.Section.Id ?? occurrence.InspectionSectionId,
        OccurrenceId = occurrence.Id,
        VideoReferenceId = occurrence.VideoReferenceId,
        Meterage = occurrence.Meterage,
        ReportField = nameof(Occurrence.ManualDescriptionOverride)
    };

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessiveWhitespace();

    [GeneratedRegex(@"\b([\p{L}\p{N}]+)\s+\1\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsecutiveWord();
}
