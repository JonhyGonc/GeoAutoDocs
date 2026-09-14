namespace GeoAutoDocs.Core.Review.Rules;

public sealed class StructureRule : IReportReviewRule
{
    public string Id => "structure.basic";
    public string Name => "Estrutura do projeto, locais e trechos";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Project.Name))
            yield return Error("Projeto sem nome", "Informe o nome do projeto.", "Project.Name");

        foreach (var group in context.SelectedSections.GroupBy(x => x.Location.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = group.First().Location;
            if (string.IsNullOrWhiteSpace(location.Name))
                yield return Error(
                    "Local sem nome",
                    "O Local selecionado para o relatório não possui identificação.",
                    "Location.Name",
                    location.Id);

            foreach (var sectionContext in group)
            {
                var section = sectionContext.Section;
                if (string.IsNullOrWhiteSpace(section.Name))
                    yield return Error(
                        "Trecho sem nome",
                        "O Trecho selecionado para o relatório não possui nome.",
                        "InspectionSection.Name",
                        location.Id,
                        section.Id);
                if (section.Diameter is <= 0)
                    yield return Error(
                        "Diâmetro inválido",
                        $"{DisplaySection(section)} possui diâmetro informado menor ou igual a zero.",
                        "InspectionSection.Diameter",
                        location.Id,
                        section.Id);
                if (section.InvestigatedMeters is <= 0)
                    yield return Error(
                        "Metragem inválida",
                        $"{DisplaySection(section)} possui metragem/extensão informada menor ou igual a zero.",
                        "InspectionSection.InvestigatedMeters",
                        location.Id,
                        section.Id);
            }

            foreach (var duplicate in group
                         .Where(x => !string.IsNullOrWhiteSpace(x.Section.Name))
                         .GroupBy(x => x.Section.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
                         .Where(x => x.Count() > 1)
                         .OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var first = duplicate.First();
                yield return new ReviewIssue
                {
                    RuleId = Id,
                    Severity = ReviewSeverity.Warning,
                    Title = "Nome de trecho duplicado",
                    Description =
                        $"O Local {DisplayLocation(location)} possui {duplicate.Count()} trechos chamados “{duplicate.Key}”.",
                    NavigationArea = ReviewNavigationArea.Inspection,
                    LocationId = location.Id,
                    SectionId = first.Section.Id,
                    ReportField = "InspectionSection.Name"
                };
            }
        }
    }

    private ReviewIssue Error(
        string title,
        string description,
        string field,
        Guid? locationId = null,
        Guid? sectionId = null) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Error,
        Title = title,
        Description = description,
        NavigationArea = locationId is null && sectionId is null
            ? ReviewNavigationArea.Report
            : ReviewNavigationArea.Inspection,
        LocationId = locationId,
        SectionId = sectionId,
        ReportField = field
    };

    private static string DisplayLocation(Location location) =>
        string.IsNullOrWhiteSpace(location.Name) ? "(sem nome)" : location.Name.Trim();

    private static string DisplaySection(InspectionSection section) =>
        string.IsNullOrWhiteSpace(section.Name) ? "O trecho" : $"O trecho {section.Name.Trim()}";
}
