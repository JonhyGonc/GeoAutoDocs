namespace GeoAutoDocs.Core.Review.Rules;

public sealed class FileReferenceRule : IReportReviewRule
{
    public string Id => "files.references";
    public string Name => "Referências de arquivos do relatório";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.TemplatePath) ||
            !context.FileSystem.FileExists(context.TemplatePath))
            yield return Error("Template Word", "Template Word não encontrado.", "TemplatePath");

        foreach (var issue in OptionalImage(
                     context, context.Report.LogoPath, "Logo do cliente", "LogoPath"))
            yield return issue;
        foreach (var issue in OptionalImage(
                     context, context.Report.LocationImagePath, "Imagem de localização", "LocationImagePath"))
            yield return issue;

        foreach (var image in context.Report.DevelopmentImages.OrderBy(x => x.Order).ThenBy(x => x.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.FilePath) ||
                !context.FileSystem.FileExists(image.FilePath))
                yield return Error(
                    "Imagem da Seção 3",
                    $"Imagem da Seção 3 não encontrada: {FriendlyFileName(image.FilePath)}.",
                    "DevelopmentImages");
            else if (!IsSupportedImage(image.FilePath))
                yield return Error(
                    "Formato de imagem",
                    $"Formato de imagem da Seção 3 não suportado: {FriendlyFileName(image.FilePath)}.",
                    "DevelopmentImages");
        }

        foreach (var image in context.Report.CustomSections
                     .OrderBy(x => x.Order).ThenBy(x => x.Id)
                     .SelectMany(x => x.Images.OrderBy(y => y.Order).ThenBy(y => y.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var issue in OptionalImage(
                         context, image.FilePath, "Imagem de seção adicional", "CustomSections", true))
                yield return issue;
        }

        foreach (var evidence in context.Evidences.OrderBy(x => x.Order).ThenBy(x => x.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrence = context.Occurrences.FirstOrDefault(x => x.Id == evidence.OccurrenceId);
            context.TryGetSection(occurrence?.InspectionSectionId ?? Guid.Empty, out var section);

            if (string.IsNullOrWhiteSpace(evidence.FilePath) ||
                !context.FileSystem.FileExists(evidence.FilePath))
                yield return Error(
                    "Imagem/evidência",
                    $"Imagem/evidência não encontrada: {FriendlyFileName(evidence.FilePath)}.",
                    "Evidence",
                    section,
                    occurrence,
                    evidence);
            else if (!IsSupportedImage(evidence.FilePath))
                yield return Error(
                    "Formato de evidência",
                    $"Formato de evidência não suportado: {FriendlyFileName(evidence.FilePath)}.",
                    "Evidence",
                    section,
                    occurrence,
                    evidence);
        }
    }

    private IEnumerable<ReviewIssue> OptionalImage(
        ReportReviewContext context,
        string? path,
        string label,
        string field,
        bool required = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (required) yield return Error(label, $"{label} não informada.", field);
            yield break;
        }

        if (!context.FileSystem.FileExists(path))
            yield return Error(label, $"{label} não encontrada: {FriendlyFileName(path)}.", field);
        else if (!IsSupportedImage(path))
            yield return Error(
                $"Formato de {label.ToLowerInvariant()}",
                $"Formato de {label.ToLowerInvariant()} não suportado: {FriendlyFileName(path)}.",
                field);
    }

    private ReviewIssue Error(
        string title,
        string description,
        string field,
        ReviewSectionContext? section = null,
        Occurrence? occurrence = null,
        Evidence? evidence = null) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Error,
        Title = title,
        Description = description,
        ReportField = field,
        LocationId = section?.Location.Id,
        SectionId = section?.Section.Id,
        OccurrenceId = occurrence?.Id,
        EvidenceId = evidence?.Id,
        CaptureId = evidence?.CaptureId,
        VideoReferenceId = evidence?.VideoReferenceId,
        Meterage = occurrence?.Meterage
    };

    private static string FriendlyFileName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "arquivo não informado" : Path.GetFileName(path);

    private static bool IsSupportedImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
}
