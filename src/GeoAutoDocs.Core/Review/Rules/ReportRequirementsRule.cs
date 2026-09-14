namespace GeoAutoDocs.Core.Review.Rules;

public sealed class ReportRequirementsRule : IReportReviewRule
{
    public string Id => "report.required";
    public string Name => "Campos obrigatórios do relatório";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        var report = context.Report;
        foreach (var issue in Required(report.Client, "Client", "Cliente")) yield return issue;
        foreach (var issue in Required(report.Area, "Area", "Área/Localização")) yield return issue;
        foreach (var issue in Required(report.ReportCode, "ReportCode", "Código do relatório")) yield return issue;
        foreach (var issue in Required(report.Contract, "Contract", "Contrato")) yield return issue;
        foreach (var issue in Required(report.PurchaseOrder, "PurchaseOrder", "Pedido de compra")) yield return issue;

        if (report.ReportDate is null)
            yield return Error("Data do relatório", "Informe a data do relatório.", "ReportDate");
        if (report.TotalInvestigatedMeters is null or <= 0)
            yield return Error("Metragem total", "Informe a metragem total investigada.", "TotalInvestigatedMeters");
        if (report.Diameters == StandardDiameterSelection.None &&
            string.IsNullOrWhiteSpace(report.CustomDiameters))
            yield return Error("Diâmetro filmado", "Selecione ao menos um diâmetro filmado.", "Diameters");
        if (context.SelectedSections.Count == 0)
            yield return Error("Trechos do relatório", "Selecione ao menos um trecho para o relatório.", "SectionLinks");
    }

    private IEnumerable<ReviewIssue> Required(string? value, string field, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield return Error(label, $"Preencha o campo obrigatório: {label}.", field);
    }

    private ReviewIssue Error(string title, string description, string field) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Error,
        Title = title,
        Description = description,
        NavigationArea = ReviewNavigationArea.Report,
        ReportField = field
    };
}
