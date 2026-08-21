using System.Globalization;

namespace GeoAutoDocs.Core;

public static class MeasurementText
{
    private const NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign |
                                                        NumberStyles.AllowDecimalPoint |
                                                        NumberStyles.AllowLeadingWhite |
                                                        NumberStyles.AllowTrailingWhite;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Format(decimal? value) => value?.ToString("0.##", PtBr) ?? string.Empty;

    public static bool TryParse(string? text, out decimal value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var input = text.Trim();
        if (input.Contains(',') && input.Contains('.')) return false;
        var culture = input.Contains('.') ? CultureInfo.InvariantCulture : PtBr;
        return decimal.TryParse(input, DecimalStyles, culture, out value);
    }
}
