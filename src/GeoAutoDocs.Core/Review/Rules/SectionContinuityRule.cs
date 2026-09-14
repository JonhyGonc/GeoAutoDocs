using System.Globalization;
using System.Text;

namespace GeoAutoDocs.Core.Review.Rules;

public sealed class SectionContinuityRule : IReportReviewRule
{
    public string Id => "section.continuity";
    public string Name => "Continuidade entre trechos";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Options.CheckSectionContinuity) yield break;

        foreach (var item in context.SelectedSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Comparable(item.Section.StartPoint, item.Section.EndPoint))
            {
                yield return Issue(
                    item,
                    "Origem e destino iguais",
                    $"{Display(item.Section)} possui origem e destino informados como “{item.Section.StartPoint!.Trim()}”.",
                    "Confira se os pontos inicial e final do trecho foram preenchidos corretamente.");
            }
        }

        for (var index = 1; index < context.SelectedSections.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = context.SelectedSections[index - 1];
            var current = context.SelectedSections[index];
            if (string.IsNullOrWhiteSpace(previous.Section.EndPoint) ||
                string.IsNullOrWhiteSpace(current.Section.StartPoint) ||
                Comparable(previous.Section.EndPoint, current.Section.StartPoint))
                continue;

            yield return Issue(
                current,
                "Possível quebra de continuidade",
                $"{Display(previous.Section)} termina em “{previous.Section.EndPoint.Trim()}”, " +
                $"mas {Display(current.Section).ToLowerInvariant()} começa em “{current.Section.StartPoint.Trim()}”.",
                "Confira a ligação entre o destino do trecho anterior e a origem deste trecho.");
        }
    }

    private ReviewIssue Issue(
        ReviewSectionContext item,
        string title,
        string description,
        string suggestedFix) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Warning,
        Title = title,
        Description = description,
        NavigationArea = ReviewNavigationArea.Inspection,
        LocationId = item.Location.Id,
        SectionId = item.Section.Id,
        ReportField = nameof(InspectionSection.StartPoint),
        SuggestedFix = suggestedFix
    };

    private static bool Comparable(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark ||
                char.IsWhiteSpace(character) ||
                character is '.' or '-')
                continue;
            result.Append(char.ToUpperInvariant(character));
        }
        return result.ToString();
    }

    private static string Display(InspectionSection section) =>
        string.IsNullOrWhiteSpace(section.Name)
            ? "O trecho"
            : $"O trecho “{section.Name.Trim()}”";
}
