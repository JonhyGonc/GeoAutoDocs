using System.Globalization;

namespace GeoAutoDocs.Core;

public sealed record SectionRename(Guid SectionId, string OldName, string NewName);

public sealed record NumberedSectionName(string Prefix, int Number);

public static class SectionNumbering
{
    // "Grelha 32" -> prefix "Grelha ", number 32. Names without a trailing number are not numbered.
    public static bool TryParse(string? name, out NumberedSectionName? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var trimmed = name.Trim();
        var start = trimmed.Length;
        while (start > 0 && char.IsAsciiDigit(trimmed[start - 1])) start--;
        if (start == trimmed.Length || start == 0) return false;
        if (string.IsNullOrWhiteSpace(trimmed[..start])) return false;
        if (!int.TryParse(trimmed[start..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false;

        parsed = new NumberedSectionName(trimmed[..start], number);
        return true;
    }

    // Plans the renumbering needed to insert newName into a sequence.
    // Every section sharing the same prefix with a number >= the new one moves up by one.
    // Shifting a whole tail keeps the sequence free of collisions, and other names are untouched.
    public static IReadOnlyList<SectionRename> PlanInsertion(IEnumerable<InspectionSection> sections, string newName)
    {
        if (!TryParse(newName, out var inserted) || inserted is null) return [];

        var renames = new List<SectionRename>();
        foreach (var section in sections)
        {
            if (!TryParse(section.Name, out var current) || current is null) continue;
            if (!string.Equals(current.Prefix, inserted.Prefix, StringComparison.Ordinal)) continue;
            if (current.Number < inserted.Number) continue;

            renames.Add(new SectionRename(section.Id, section.Name,
                $"{current.Prefix}{current.Number + 1}"));
        }
        return renames;
    }
}
