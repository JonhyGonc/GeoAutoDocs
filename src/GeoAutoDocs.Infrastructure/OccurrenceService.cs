using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed class OccurrenceService(AppDataPaths paths, LocalLogger logger)
{
    private static readonly HashSet<string> SupportedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    public Occurrence CreateDraft(InspectionSection section, Occurrence? source = null, bool duplicate = false)
    {
        if (source is null)
            return new Occurrence
            {
                InspectionSectionId = section.Id, LocationMode = OccurrenceLocationMode.Point,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                Order = section.Occurrences.Count == 0 ? 0 : section.Occurrences.Max(x => x.Order) + 1
            };

        var draft = new Occurrence
        {
            Id = duplicate ? Guid.NewGuid() : source.Id, InspectionSectionId = source.InspectionSectionId,
            VideoReferenceId = duplicate ? null : source.VideoReferenceId,
            VideoTimestamp = duplicate ? null : source.VideoTimestamp,
            LocationMode = source.LocationMode,
            Meterage = duplicate ? null : source.Meterage, EndMeterage = duplicate ? null : source.EndMeterage,
            ReferencePoint = source.ReferencePoint, Position = source.Position, Notes = source.Notes,
            CustomGeneratedText = duplicate ? null : source.CustomGeneratedText,
            GeneratedDescription = duplicate ? string.Empty : source.GeneratedDescription,
            ManualDescriptionOverride = duplicate ? null : source.ManualDescriptionOverride,
            InterruptedRecording = !duplicate && source.InterruptedRecording,
            CreatedAt = duplicate ? DateTime.UtcNow : source.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Order = duplicate ? (section.Occurrences.Count == 0 ? 0 : section.Occurrences.Max(x => x.Order) + 1) : source.Order,
            Anomalies = source.Anomalies.OrderBy(x => x.Order).Select(x => new OccurrenceAnomaly
            {
                OccurrenceId = duplicate ? Guid.Empty : source.Id, AnomalyTypeId = x.AnomalyTypeId,
                Position = x.Position, Orientation = x.Orientation,
                ApproximateExtension = x.ApproximateExtension, Dimension = x.Dimension,
                Condition = x.Condition, InfiltrationCondition = x.InfiltrationCondition,
                EstimatedPercentage = x.EstimatedPercentage, Notes = x.Notes,
                CustomText = x.CustomText, Order = x.Order, AnomalyType = x.AnomalyType
            }).ToList(),
            Evidence = duplicate ? [] : source.Evidence.OrderBy(x => x.Order).Select(x => new Evidence
            {
                Id = x.Id, OccurrenceId = source.Id, CaptureId = x.CaptureId, SourceType = x.SourceType,
                VideoReferenceId = x.VideoReferenceId, FilePath = x.FilePath,
                VideoTimestamp = x.VideoTimestamp, Order = x.Order, Caption = x.Caption,
                IsPrimary = x.IsPrimary, ManagedByGeoAutoDocs = x.ManagedByGeoAutoDocs, SharedFilePath = x.SharedFilePath
            }).ToList()
        };
        foreach (var item in draft.Anomalies) item.OccurrenceId = draft.Id;
        return draft;
    }

    public async Task<Occurrence> SaveAsync(Project project, InspectionSection section, Occurrence draft,
        bool isReadOnly, CancellationToken cancellationToken = default)
    {
        EnsureWritable(isReadOnly);
        if (draft.InspectionSectionId != section.Id)
            throw new InvalidOperationException("A anomalia não pertence ao trecho selecionado.");

        NormalizeLocation(draft);
        NormalizeChildren(draft);
        OccurrenceRules.Validate(draft);
        await PrepareEvidenceAsync(project, section, draft, cancellationToken);
        draft.GeneratedDescription = OccurrenceRules.GenerateDescription(draft);
        draft.ManualDescriptionOverride = EmptyToNull(draft.ManualDescriptionOverride);
        draft.ReferencePoint = EmptyToNull(draft.ReferencePoint);
        draft.Notes = EmptyToNull(draft.Notes);
        var now = DateTime.UtcNow;
        if (draft.CreatedAt == default) draft.CreatedAt = now;
        draft.UpdatedAt = now;

        var existingIndex = section.Occurrences.FindIndex(x => x.Id == draft.Id);
        if (existingIndex >= 0) section.Occurrences[existingIndex] = draft;
        else section.Occurrences.Add(draft);
        RefreshCaptureStatuses(project);
        logger.Info($"Anomalia {draft.Id} salva no trecho {section.Id} com {draft.Anomalies.Count} tipo(s) e {draft.Evidence.Count} evidência(s).");
        return draft;
    }

    public void Delete(Project project, InspectionSection section, Occurrence occurrence, bool isReadOnly)
    {
        EnsureWritable(isReadOnly);
        if (!section.Occurrences.Remove(occurrence)) return;
        RefreshCaptureStatuses(project);
        logger.Info($"Anomalia {occurrence.Id} excluída do trecho {section.Id}.");
    }

    public static Evidence FromCapture(Capture capture) => new()
    {
        CaptureId = capture.Id, SourceType = EvidenceSourceType.VideoCapture,
        VideoReferenceId = capture.VideoReferenceId, VideoTimestamp = capture.VideoTimestamp,
        FilePath = capture.FilePath, ManagedByGeoAutoDocs = false
    };

    public static Evidence FromExternalImage(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Imagem externa não encontrada.", path);
        if (!SupportedImageExtensions.Contains(Path.GetExtension(path)))
            throw new InvalidDataException("Use uma imagem PNG, JPG ou JPEG.");
        return new Evidence { SourceType = EvidenceSourceType.ExternalImage, FilePath = Path.GetFullPath(path) };
    }

    public string GetManagedEvidenceDirectory(Project project) => project.StorageKind == ProjectStorageKind.Shared
        ? Path.Combine(paths.CacheDirectory, project.Id.ToString("N"), "evidence")
        : Path.Combine(project.ProjectPath ?? Path.Combine(paths.ProjectsDirectory, project.Id.ToString("N")), "evidence");

    private async Task PrepareEvidenceAsync(Project project, InspectionSection section, Occurrence occurrence,
        CancellationToken cancellationToken)
    {
        var assignedElsewhere = project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
            .Where(x => x.Id != occurrence.Id).SelectMany(x => x.Evidence)
            .Where(x => x.CaptureId is not null).Select(x => x.CaptureId!.Value).ToHashSet();
        var captureIds = occurrence.Evidence.Where(x => x.CaptureId is not null)
            .Select(x => x.CaptureId!.Value).ToList();
        if (captureIds.Distinct().Count() != captureIds.Count)
            throw new InvalidOperationException("A mesma captura foi adicionada mais de uma vez.");

        foreach (var evidence in occurrence.Evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence.CaptureId is Guid captureId)
            {
                if (assignedElsewhere.Contains(captureId))
                    throw new InvalidOperationException("Uma das capturas já pertence a outra anomalia.");
                var capture = project.Captures.SingleOrDefault(x => x.Id == captureId)
                    ?? throw new InvalidOperationException("Captura de evidência não encontrada no projeto.");
                if (capture.SectionId != section.Id)
                    throw new InvalidOperationException("A evidência deve pertencer ao mesmo trecho da anomalia.");
                evidence.SourceType = EvidenceSourceType.VideoCapture;
                evidence.FilePath = capture.FilePath;
                evidence.VideoReferenceId = capture.VideoReferenceId;
                evidence.VideoTimestamp = capture.VideoTimestamp;
                evidence.ManagedByGeoAutoDocs = false;
            }
            else
            {
                if (!File.Exists(evidence.FilePath))
                    throw new FileNotFoundException("Imagem de evidência não encontrada.", evidence.FilePath);
                if (!SupportedImageExtensions.Contains(Path.GetExtension(evidence.FilePath)))
                    throw new InvalidDataException("Use uma imagem PNG, JPG ou JPEG.");
                evidence.SourceType = EvidenceSourceType.ExternalImage;
                evidence.VideoReferenceId = null;
                evidence.VideoTimestamp = null;
                if (!evidence.ManagedByGeoAutoDocs)
                {
                    var directory = GetManagedEvidenceDirectory(project);
                    Directory.CreateDirectory(directory);
                    var target = Path.Combine(directory, $"evidence_{evidence.Id:N}{Path.GetExtension(evidence.FilePath).ToLowerInvariant()}");
                    await CopyAtomicallyAsync(evidence.FilePath, target, cancellationToken);
                    evidence.FilePath = target;
                    evidence.ManagedByGeoAutoDocs = true;
                }
            }
        }
    }

    private static void NormalizeLocation(Occurrence occurrence)
    {
        if (occurrence.LocationMode == OccurrenceLocationMode.Point) occurrence.EndMeterage = null;
        if (occurrence.LocationMode == OccurrenceLocationMode.EntireSection)
        {
            occurrence.Meterage = null;
            occurrence.EndMeterage = null;
        }
    }

    private static void NormalizeChildren(Occurrence occurrence)
    {
        for (var i = 0; i < occurrence.Anomalies.Count; i++)
        {
            occurrence.Anomalies[i].OccurrenceId = occurrence.Id;
            occurrence.Anomalies[i].Order = i;
            occurrence.Anomalies[i].CustomText = EmptyToNull(occurrence.Anomalies[i].CustomText);
            occurrence.Anomalies[i].Dimension = EmptyToNull(occurrence.Anomalies[i].Dimension);
            occurrence.Anomalies[i].Notes = EmptyToNull(occurrence.Anomalies[i].Notes);
        }
        for (var i = 0; i < occurrence.Evidence.Count; i++)
        {
            occurrence.Evidence[i].OccurrenceId = occurrence.Id;
            occurrence.Evidence[i].Order = i;
        }
        if (occurrence.Evidence.Count > 0 && occurrence.Evidence.All(x => !x.IsPrimary))
            occurrence.Evidence[0].IsPrimary = true;
        var primaryFound = false;
        foreach (var evidence in occurrence.Evidence)
        {
            if (!evidence.IsPrimary) continue;
            if (!primaryFound) primaryFound = true;
            else evidence.IsPrimary = false;
        }
    }

    private static void RefreshCaptureStatuses(Project project)
    {
        var assigned = project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
            .SelectMany(x => x.Evidence).Where(x => x.CaptureId is not null)
            .Select(x => x.CaptureId!.Value).ToHashSet();
        foreach (var capture in project.Captures)
            capture.Status = assigned.Contains(capture.Id) ? CaptureStatus.Assigned : CaptureStatus.Pending;
    }

    private static async Task CopyAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
        var temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureWritable(bool isReadOnly)
    {
        if (isReadOnly) throw new InvalidOperationException("O projeto está em modo somente leitura.");
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
