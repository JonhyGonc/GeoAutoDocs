using System.Globalization;
using System.Text.RegularExpressions;

namespace GeoAutoDocs.Core.Review.Rules;

public sealed partial class SectionSequenceRule : IReportReviewRule
{
    private sealed record NumberedSection(
        ReviewSectionContext Context,
        string Name,
        string Stem,
        string StemKey,
        int Number,
        int NumberWidth);

    public string Id => "section.sequence";
    public string Name => "Sequência dos trechos";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Options.CheckSectionSequence) yield break;

        var groups = context.SelectedSections
            .Select(Parse)
            .Where(x => x is not null)
            .Cast<NumberedSection>()
            .GroupBy(x => x.StemKey, StringComparer.Ordinal)
            .Where(x => x.Count() > 1);

        foreach (var group in groups)
        {
            NumberedSection? previous = null;
            foreach (var current in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is null)
                {
                    previous = current;
                    continue;
                }

                if (current.Number == previous.Number)
                {
                    // Nomes iguais já são reportados por StructureRule; aqui só há issue
                    // quando a mesma numeração aparece com grafias diferentes.
                    if (current.Context.Location.Id != previous.Context.Location.Id ||
                        !string.Equals(current.Name, previous.Name, StringComparison.OrdinalIgnoreCase))
                        yield return Issue(
                            current,
                            "Numeração de trecho duplicada",
                            $"“{previous.Name}” e “{current.Name}” utilizam o número {current.Number}.",
                            "Confira os nomes e mantenha uma numeração única para cada trecho.");
                }
                else if (current.Number < previous.Number)
                {
                    yield return Issue(
                        current,
                        "Numeração de trechos fora da ordem",
                        $"“{current.Name}” aparece depois de “{previous.Name}”, mas possui numeração menor.",
                        "Confira a ordem dos trechos ou ajuste seus nomes.");
                }
                else if (current.Number > previous.Number + 1)
                {
                    var width = Math.Max(2, Math.Max(previous.NumberWidth, current.NumberWidth));
                    var firstMissing = previous.Number + 1;
                    var lastMissing = current.Number - 1;
                    var missing = firstMissing == lastMissing
                        ? $"o número {Format(firstMissing, width)} não foi encontrado"
                        : $"os números {Format(firstMissing, width)} a {Format(lastMissing, width)} não foram encontrados";
                    yield return Issue(
                        current,
                        "Possível quebra na sequência",
                        $"Entre “{previous.Name}” e “{current.Name}”, {missing}.",
                        "Confira se algum trecho não foi selecionado, removido ou nomeado incorretamente.");
                }

                previous = current;
            }
        }
    }

    internal static bool TryParseName(
        string? name,
        out string stem,
        out string stemKey,
        out int number,
        out int numberWidth)
    {
        stem = string.Empty;
        stemKey = string.Empty;
        number = 0;
        numberWidth = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var match = NumberedName().Match(name.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return false;

        stem = match.Groups["stem"].Value;
        stemKey = Whitespace().Replace(stem.Trim(), " ").ToUpperInvariant();
        numberWidth = match.Groups["number"].Value.Length;
        return stemKey.Length > 0;
    }

    private static NumberedSection? Parse(ReviewSectionContext context) =>
        TryParseName(context.Section.Name, out var stem, out var stemKey, out var number, out var width)
            ? new NumberedSection(context, context.Section.Name.Trim(), stem, stemKey, number, width)
            : null;

    private ReviewIssue Issue(
        NumberedSection item,
        string title,
        string description,
        string suggestedFix) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Warning,
        Title = title,
        Description = description,
        NavigationArea = ReviewNavigationArea.Inspection,
        LocationId = item.Context.Location.Id,
        SectionId = item.Context.Section.Id,
        ReportField = nameof(InspectionSection.Name),
        SuggestedFix = suggestedFix
    };

    private static string Format(int value, int width) =>
        value.ToString($"D{width}", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<stem>.*?\D)(?<number>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedName();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
