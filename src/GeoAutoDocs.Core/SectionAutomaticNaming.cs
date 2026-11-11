using System.Globalization;

namespace GeoAutoDocs.Core;

public sealed record SectionRenamePlanItem(
    Guid LocationId,
    string LocationName,
    Guid SectionId,
    string CurrentName,
    string ProposedName,
    int Sequence);

public static class SectionAutomaticNaming
{
    public const string DefaultPrefix = "Trecho";
    public const int DefaultStartingNumber = 1;

    public static IReadOnlyList<SectionRenamePlanItem> CreatePlan(
        Project project,
        string prefix,
        int startingNumber)
    {
        ArgumentNullException.ThrowIfNull(project);

        var normalizedPrefix = prefix?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
            throw new ArgumentException("Informe um prefixo para os nomes dos trechos.", nameof(prefix));
        if (startingNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(startingNumber), "O número inicial deve ser maior que zero.");

        var result = new List<SectionRenamePlanItem>();
        var sequence = startingNumber;

        foreach (var location in project.Locations.OrderBy(x => x.Order))
        {
            foreach (var section in location.Sections.OrderBy(x => x.Order))
            {
                result.Add(new SectionRenamePlanItem(
                    location.Id,
                    location.Name,
                    section.Id,
                    section.Name,
                    $"{normalizedPrefix} {sequence.ToString("D2", CultureInfo.InvariantCulture)}",
                    sequence));
                sequence++;
            }
        }

        return result;
    }
}
