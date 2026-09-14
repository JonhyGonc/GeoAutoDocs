namespace GeoAutoDocs.Core.Review;

public sealed record ReviewOptions
{
    public static ReviewOptions Default { get; } = new();

    public decimal DuplicateOccurrenceToleranceMeters { get; init; } = 0.10m;
    public int MinimumDescriptionLength { get; init; } = 10;
    public bool CheckDuplicateOccurrences { get; init; } = true;
    public bool CheckTextQuality { get; init; } = true;
    public bool CheckSectionSequence { get; init; } = true;
    public bool CheckSectionContinuity { get; init; } = true;
    public bool CheckNamingConsistency { get; init; } = true;

    public IReadOnlySet<string> TextPlaceholders { get; init; } =
        new HashSet<string>(StringComparer.CurrentCultureIgnoreCase)
        {
            "teste",
            "xxx",
            "xxxx",
            "-",
            "descrição",
            "anomalia",
            "digite aqui"
        };

    internal void Validate()
    {
        if (DuplicateOccurrenceToleranceMeters < 0)
            throw new ArgumentOutOfRangeException(
                nameof(DuplicateOccurrenceToleranceMeters),
                "A tolerância de duplicidade não pode ser negativa.");
        if (MinimumDescriptionLength < 0)
            throw new ArgumentOutOfRangeException(
                nameof(MinimumDescriptionLength),
                "O comprimento mínimo da descrição não pode ser negativo.");
    }
}
