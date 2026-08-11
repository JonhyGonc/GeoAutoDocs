using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed record ReportValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int SelectedSections,
    int Occurrences,
    int Evidences)
{
    public bool CanGenerate => Errors.Count == 0;
}

public static class ReportValidation
{
    public static ReportValidationResult Validate(Project project, Report report, string? templatePath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var selected = OrderedSections(project, report).ToList();

        Required(report.Client, "Cliente", errors);
        Required(report.Area, "Área/Localização", errors);
        Required(report.ReportCode, "Código do relatório", errors);
        Required(report.Contract, "Contrato", errors);
        Required(report.PurchaseOrder, "Pedido de compra", errors);
        Required(report.LocationDescription, "Descrição da localização", errors);
        if (report.ReportDate is null) errors.Add("Informe a data do relatório.");
        if (report.TotalInvestigatedMeters is null or <= 0) errors.Add("Informe a metragem total investigada.");
        if (report.Diameters == StandardDiameterSelection.None &&
            string.IsNullOrWhiteSpace(report.CustomDiameters))
            errors.Add("Selecione ao menos um diâmetro filmado.");
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            errors.Add("Template Word não encontrado.");
        if (selected.Count == 0) errors.Add("Selecione ao menos um trecho para o relatório.");

        foreach (var image in report.DevelopmentImages)
        {
            if (string.IsNullOrWhiteSpace(image.FilePath) || !File.Exists(image.FilePath))
                errors.Add($"Imagem da Seção 3 não encontrada: {image.FilePath ?? "arquivo não informado"}.");
            else if (!IsSupportedImage(image.FilePath))
                errors.Add($"Formato de imagem da Seção 3 não suportado: {image.FilePath}.");
        }

        var occurrences = selected.SelectMany(x => x.Section.Occurrences).ToList();
        var evidences = occurrences.SelectMany(x => x.Evidence).ToList();
        foreach (var item in evidences)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
                errors.Add($"Imagem/evidência não encontrada: {item.FilePath ?? "arquivo não informado"}.");
            else if (!IsSupportedImage(item.FilePath))
                errors.Add($"Formato de evidência não suportado: {item.FilePath}.");
        }

        foreach (var occurrence in occurrences.Where(x => x.Evidence.Count == 0))
            warnings.Add($"{SectionName(selected, occurrence.InspectionSectionId)} possui ocorrência sem evidência.");

        foreach (var section in selected)
        {
            var pending = project.Captures.Count(x =>
                x.SectionId == section.Section.Id && x.Status == CaptureStatus.Pending);
            if (pending > 0)
                warnings.Add($"{section.Section.Name} possui {pending} captura(s) pendente(s); elas não entrarão no relatório.");
        }

        return new ReportValidationResult(errors.Distinct().ToList(), warnings.Distinct().ToList(),
            selected.Count, occurrences.Count, evidences.Count);
    }

    public static IEnumerable<(Location Location, InspectionSection Section)> OrderedSections(
        Project project, Report report)
    {
        var selectedIds = report.SectionLinks.Select(x => x.SectionId).ToHashSet();
        return project.Locations.OrderBy(x => x.Order).ThenBy(x => x.Name)
            .SelectMany(location => location.Sections.OrderBy(x => x.Order).ThenBy(x => x.Name)
                .Where(section => selectedIds.Contains(section.Id))
                .Select(section => (location, section)));
    }

    private static string SectionName(
        IReadOnlyList<(Location Location, InspectionSection Section)> selected, Guid sectionId) =>
        selected.FirstOrDefault(x => x.Section.Id == sectionId).Section?.Name ?? "Trecho selecionado";

    private static void Required(string? value, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"Preencha o campo obrigatório: {label}.");
    }

    private static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg";
}
