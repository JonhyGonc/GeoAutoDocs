using System.Globalization;

namespace GeoAutoDocs.Core.Review.Rules;

public sealed class OccurrenceValidityRule : IReportReviewRule
{
    public string Id => "occurrence.validity";
    public string Name => "Validade das ocorrências";

    public IEnumerable<ReviewIssue> Evaluate(
        ReportReviewContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var sectionContext in context.SelectedSections)
        {
            foreach (var video in sectionContext.Section.VideoReferences.OrderBy(x => x.Order).ThenBy(x => x.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(video.FilePath) ||
                    !context.FileSystem.FileExists(video.FilePath))
                    yield return Error(
                        "Vídeo não encontrado",
                        $"O vídeo “{DisplayVideo(video)}” não foi encontrado.",
                        sectionContext,
                        videoId: video.Id,
                        field: "VideoReference.FilePath");
            }
        }

        foreach (var occurrence in context.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.TryGetOccurrenceSection(occurrence.Id, out var ownerSection);

            foreach (var failure in OccurrenceRules.GetValidationFailures(occurrence))
                yield return Error(
                    "Ocorrência inválida",
                    failure.Message,
                    ownerSection,
                    occurrence,
                    field: failure.Field);

            if (ownerSection is not null &&
                occurrence.InspectionSectionId != ownerSection.Section.Id)
                yield return Error(
                    "Referência de trecho inválida",
                    "A ocorrência possui uma referência quebrada para o Trecho.",
                    ownerSection,
                    occurrence,
                    field: nameof(Occurrence.InspectionSectionId));

            if (occurrence.VideoReferenceId is Guid videoId &&
                !context.TryGetVideo(videoId, out _))
                yield return Error(
                    "Referência de vídeo inválida",
                    "A ocorrência referencia um vídeo que não pertence aos trechos selecionados.",
                    ownerSection,
                    occurrence,
                    videoId: videoId,
                    field: nameof(Occurrence.VideoReferenceId));

            foreach (var anomaly in occurrence.Anomalies.OrderBy(x => x.Order))
            {
                if (anomaly.AnomalyType is not null &&
                    anomaly.AnomalyType.Id != anomaly.AnomalyTypeId)
                    yield return Error(
                        "Tipo de anomalia inválido",
                        $"A anomalia “{anomaly.DisplayName}” possui uma referência de catálogo inconsistente.",
                        ownerSection,
                        occurrence,
                        field: nameof(OccurrenceAnomaly.AnomalyTypeId));
                else if (!context.TryGetAnomalyDefinition(anomaly.AnomalyTypeId, out _))
                    yield return Error(
                        "Tipo de anomalia inexistente",
                        $"O tipo de anomalia “{anomaly.DisplayName}” não existe no catálogo carregado.",
                        ownerSection,
                        occurrence,
                        field: nameof(OccurrenceAnomaly.AnomalyTypeId));
            }

            if (ownerSection?.Section.InvestigatedMeters is > 0m &&
                MaximumMeterage(occurrence) is decimal maximum &&
                maximum > ownerSection.Section.InvestigatedMeters.Value)
                yield return Error(
                    "Ocorrência fora dos limites do trecho",
                    $"A ocorrência está em {maximum.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))} m, além da extensão cadastrada de " +
                    $"{ownerSection.Section.InvestigatedMeters.Value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))} m.",
                    ownerSection,
                    occurrence,
                    field: nameof(Occurrence.Meterage),
                    meterage: maximum,
                    canBeAcceptedAfterVerification: true);

            foreach (var evidence in occurrence.Evidence.OrderBy(x => x.Order).ThenBy(x => x.Id))
            {
                if (evidence.OccurrenceId != occurrence.Id)
                    yield return Error(
                        "Referência de evidência inválida",
                        "A evidência possui uma referência quebrada para a ocorrência.",
                        ownerSection,
                        occurrence,
                        evidence,
                        field: nameof(Evidence.OccurrenceId));
                if (evidence.CaptureId is Guid captureId &&
                    !context.TryGetCapture(captureId, out _))
                    yield return Error(
                        "Referência de captura inválida",
                        "A evidência referencia uma captura inexistente.",
                        ownerSection,
                        occurrence,
                        evidence,
                        captureId: captureId,
                        field: nameof(Evidence.CaptureId));
                if (evidence.VideoReferenceId is Guid evidenceVideoId &&
                    !context.TryGetVideo(evidenceVideoId, out _))
                    yield return Error(
                        "Referência de vídeo inválida",
                        "A evidência referencia um vídeo inexistente.",
                        ownerSection,
                        occurrence,
                        evidence,
                        videoId: evidenceVideoId,
                        field: nameof(Evidence.VideoReferenceId));
            }
        }
    }

    private ReviewIssue Error(
        string title,
        string description,
        ReviewSectionContext? section,
        Occurrence? occurrence = null,
        Evidence? evidence = null,
        Guid? captureId = null,
        Guid? videoId = null,
        string? field = null,
        decimal? meterage = null,
        bool canBeAcceptedAfterVerification = false) => new()
    {
        RuleId = Id,
        Severity = ReviewSeverity.Error,
        CanBeAcceptedAfterVerification = canBeAcceptedAfterVerification,
        Title = title,
        Description = description,
        NavigationArea = ReviewNavigationArea.Inspection,
        LocationId = section?.Location.Id,
        SectionId = section?.Section.Id ?? occurrence?.InspectionSectionId,
        OccurrenceId = occurrence?.Id,
        CaptureId = captureId ?? evidence?.CaptureId,
        VideoReferenceId = videoId ?? evidence?.VideoReferenceId ?? occurrence?.VideoReferenceId,
        EvidenceId = evidence?.Id,
        ReportField = field,
        Meterage = meterage ?? occurrence?.Meterage
    };

    private static decimal? MaximumMeterage(Occurrence occurrence) => occurrence.LocationMode switch
    {
        OccurrenceLocationMode.Point or OccurrenceLocationMode.From => occurrence.Meterage,
        OccurrenceLocationMode.Range => occurrence.EndMeterage ?? occurrence.Meterage,
        OccurrenceLocationMode.MultiPoint when occurrence.MeteragePoints.Count > 0 =>
            occurrence.MeteragePoints.Max(x => x.Meterage),
        _ => null
    };

    private static string DisplayVideo(VideoReference video) =>
        string.IsNullOrWhiteSpace(video.DisplayName)
            ? Path.GetFileName(video.FilePath)
            : video.DisplayName.Trim();
}
