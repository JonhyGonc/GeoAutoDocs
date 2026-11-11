using System.Globalization;

namespace GeoAutoDocs.Core.Review.Rules;

public sealed class SectionCompletionRule : IReportReviewRule
{
    public string Id => "section.completion";
    public string Name => "Encerramento e metragem final dos trechos";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var sectionContext in context.SelectedSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = sectionContext.Section;
            var terminal = OccurrenceRules.GetTerminalOccurrence(section);
            if (terminal is null) continue;
            var finalMeterage = OccurrenceRules.GetFinalMeterage(terminal);

            if (!terminal.InterruptedRecording)
                yield return Error(
                    "Última anomalia sem interrupção da gravação",
                    "Marque “Esta anomalia interrompeu a gravação” na anomalia de maior metragem do trecho.",
                    sectionContext,
                    terminal,
                    nameof(Occurrence.InterruptedRecording),
                    finalMeterage);

            if (finalMeterage is null)
            {
                yield return Error(
                    "Última anomalia sem metragem final",
                    "A anomalia considerada final precisa possuir uma metragem para definir a metragem inspecionada do trecho.",
                    sectionContext,
                    terminal,
                    nameof(Occurrence.Meterage));
                continue;
            }

            if (section.InvestigatedMeters != finalMeterage)
                yield return Error(
                    "Metragem inspecionada divergente",
                    $"A metragem inspecionada do trecho deve ser {Format(finalMeterage.Value)} m, igual à maior metragem registrada nas anomalias. Valor atual: {Format(section.InvestigatedMeters)} m.",
                    sectionContext,
                    terminal,
                    nameof(InspectionSection.InvestigatedMeters),
                    finalMeterage);
        }
    }

    private ReviewIssue Error(
        string title,
        string description,
        ReviewSectionContext section,
        Occurrence occurrence,
        string field,
        decimal? meterage = null) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Error,
        Title = title,
        Description = description,
        NavigationArea = ReviewNavigationArea.Inspection,
        LocationId = section.Location.Id,
        SectionId = section.Section.Id,
        OccurrenceId = occurrence.Id,
        ReportField = field,
        Meterage = meterage
    };

    private static string Format(decimal? value) =>
        value?.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) ?? "não informada";
}
