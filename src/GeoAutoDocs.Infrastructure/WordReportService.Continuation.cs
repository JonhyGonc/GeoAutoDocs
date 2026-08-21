using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public enum ReportContinuationItemStatus { Existing, New, Modified, Removed, AssociationError }

public sealed class ReportContinuationItem
{
    public Guid OccurrenceId { get; init; }
    public Guid LocationId { get; init; }
    public Guid SectionId { get; init; }
    public string LocationName { get; init; } = string.Empty;
    public string SectionName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ReportContinuationItemStatus Status { get; init; }
    public bool HasMissingImage { get; init; }
    public int EvidenceCount { get; init; }
    public int MissingImageCount { get; init; }
    public string CurrentHash { get; init; } = string.Empty;
    public string PreviousHash { get; init; } = string.Empty;
    public bool IsSelected { get; set; }
    public bool CanSelect => Status is ReportContinuationItemStatus.New or ReportContinuationItemStatus.Modified;
    public string StatusText => Status switch
    {
        ReportContinuationItemStatus.Existing => "Já exportada",
        ReportContinuationItemStatus.New => HasMissingImage ? "Nova • imagem ausente" : "Nova",
        ReportContinuationItemStatus.Modified => HasMissingImage ? "Alterada • imagem ausente" : "Alterada",
        ReportContinuationItemStatus.Removed => "Removida no projeto (mantida no DOCX)",
        _ => "Erro de associação"
    };
    public string ContextText => $"{LocationName} • {SectionName}";
}

public sealed class ReportContinuationAnalysis
{
    public string SourcePath { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public bool HasManifest { get; init; }
    public bool RequiresManualReview { get; init; }
    public int PageCount { get; init; }
    public int LocationCount { get; init; }
    public int SectionCount { get; init; }
    public int ExistingCount { get; init; }
    public int NewCount { get; init; }
    public int ModifiedCount { get; init; }
    public int RemovedCount { get; init; }
    public int MissingImageCount { get; init; }
    public int LastFigureNumber { get; init; }
    public int NextVersion { get; init; }
    public string SuggestedOutputPath { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<ReportContinuationItem> Items { get; init; } = [];
    public bool CanGenerate => Errors.Count == 0 && Items.Any(x => x.CanSelect);
    internal ReportExportManifest Manifest { get; init; } = new();
}

public sealed class ReportContinuationResult
{
    public string BaseFilePath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string BaseFileHash { get; init; } = string.Empty;
    public int Version { get; init; }
    public int AddedOccurrences { get; init; }
    public int AddedEvidences { get; init; }
    public string ManifestJson { get; init; } = string.Empty;
}

internal sealed class ReportExportManifest
{
    public int FormatVersion { get; set; } = 1;
    public string TemplateVersion { get; set; } = "GeoAutoDocs.WordReport.v1";
    public Guid ProjectId { get; set; }
    public Guid ReportId { get; set; }
    public int ReportVersion { get; set; }
    public string BaseDocumentHash { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string InsertionPoint { get; set; } = "GEOAUTODOCS_ANOMALIES_END";
    public int LastFigureNumber { get; set; }
    public List<ReportExportOccurrence> Occurrences { get; set; } = [];
}

internal sealed class ReportExportOccurrence
{
    public Guid LocationId { get; set; }
    public Guid SectionId { get; set; }
    public Guid OccurrenceId { get; set; }
    public string OccurrenceHash { get; set; } = string.Empty;
    public List<ReportExportEvidence> Evidence { get; set; } = [];
}

internal sealed class ReportExportEvidence
{
    public Guid EvidenceId { get; set; }
    public string EvidenceHash { get; set; } = string.Empty;
    public int FigureNumber { get; set; }
}

public sealed partial class WordReportService
{
    private const string ContinuationBookmarkName = "GEOAUTODOCS_ANOMALIES_END";
    private const string ManifestNamespace = "urn:geoautodocs:report-manifest:v1";
    private const long MaxDocxBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const int MaxZipEntries = 10000;
    private const int MaxImages = 5000;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public Task<ReportContinuationAnalysis> AnalyzeContinuationAsync(Project project, Report report, string sourcePath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
            AnalyzeContinuation(project, report, sourcePath, cancellationToken), cancellationToken);

    public Task<ReportContinuationResult> ContinueReportAsync(Project project, Report report, string sourcePath,
        IEnumerable<Guid> selectedOccurrenceIds, bool confirmLegacyReview,
        CancellationToken cancellationToken = default) => Task.Run(() =>
            ContinueReport(project, report, sourcePath, selectedOccurrenceIds, confirmLegacyReview, cancellationToken),
            cancellationToken);

    private ReportContinuationAnalysis AnalyzeContinuation(Project project, Report report, string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (report.ProjectId != project.Id)
            throw new InvalidOperationException("O relatório não pertence ao projeto selecionado.");
        var fullPath = ValidateDocxPackage(sourcePath);
        var sourceHash = ComputeFileHash(fullPath);
        var errors = new List<string>();
        var warnings = new List<string>();
        ReportExportManifest? storedManifest;
        string documentText;
        int pageCount;
        int lastFigure;
        int imageCount;
        bool hasInsertionPoint;

        using (var packageStream = File.Open(fullPath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var document = WordprocessingDocument.Open(packageStream, false))
        {
            var mainPart = document.MainDocumentPart ?? throw new InvalidDataException("DOCX sem documento principal.");
            var body = mainPart.Document.Body ?? throw new InvalidDataException("DOCX sem corpo principal.");
            storedManifest = ReadContinuationManifest(mainPart);
            documentText = string.Join("\n", body.Descendants<Paragraph>().Select(ParagraphText));
            pageCount = ReadStoredPageCount(document);
            lastFigure = DetectLastFigureNumber(body);
            imageCount = mainPart.ImageParts.Count();
            hasInsertionPoint = FindContinuationAnchor(body) is not null || FindSignatureStart(body) is not null;
            if (!hasInsertionPoint)
                errors.Add("Não foi possível localizar o ponto seguro de inserção antes da assinatura.");
            if (!HasContinuationTemplates(body, project, DetectFigureSequence(body).Chapter))
                errors.Add("O relatório não contém os estilos mínimos de local, trecho, anomalia, imagem e legenda.");
        }

        if (imageCount > MaxImages) errors.Add($"O DOCX excede o limite de {MaxImages} imagens.");
        var hasManifest = storedManifest is not null;
        var requiresReview = false;
        var manifest = storedManifest ?? BuildLegacyManifest(project, report, documentText, lastFigure,
            warnings, ref requiresReview);
        if (manifest.ProjectId != Guid.Empty && manifest.ProjectId != project.Id)
            errors.Add("O DOCX pertence a outro projeto.");
        if (manifest.ReportId != Guid.Empty && manifest.ReportId != report.Id)
        {
            if (manifest.ProjectId == project.Id)
            {
                warnings.Add("O DOCX está vinculado a outro relatório deste projeto. " +
                             "Confirme a revisão para vinculá-lo ao relatório atual.");
                requiresReview = true;
            }
            else
            {
                errors.Add("O DOCX pertence a outro relatório.");
            }
        }

        if (!hasManifest)
        {
            warnings.Insert(0, "Relatório antigo sem manifesto: a correspondência foi feita por texto e exige revisão.");
            requiresReview = true;
        }

        var current = EnumerateReportOccurrences(project, report).ToList();
        var previousById = manifest.Occurrences.GroupBy(x => x.OccurrenceId).ToDictionary(x => x.Key, x => x.Last());
        var items = new List<ReportContinuationItem>();
        foreach (var item in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = ComputeOccurrenceHash(item.Occurrence);
            previousById.TryGetValue(item.Occurrence.Id, out var previous);
            var associationError = item.Occurrence.Evidence.Any(x => x.OccurrenceId != item.Occurrence.Id);
            var missing = item.Occurrence.Evidence.Count(x => !IsSupportedImageFile(x.FilePath));
            var status = associationError ? ReportContinuationItemStatus.AssociationError
                : previous is null ? ReportContinuationItemStatus.New
                : string.Equals(previous.OccurrenceHash, hash, StringComparison.OrdinalIgnoreCase)
                    ? ReportContinuationItemStatus.Existing : ReportContinuationItemStatus.Modified;
            items.Add(new ReportContinuationItem
            {
                OccurrenceId = item.Occurrence.Id,
                LocationId = item.Location.Id,
                SectionId = item.Section.Id,
                LocationName = item.Location.Name,
                SectionName = item.Section.Name,
                Description = FormatOccurrenceForReport(item.Occurrence),
                Status = status,
                HasMissingImage = missing > 0,
                MissingImageCount = missing,
                EvidenceCount = item.Occurrence.Evidence.Count,
                CurrentHash = hash,
                PreviousHash = previous?.OccurrenceHash ?? string.Empty,
                IsSelected = status == ReportContinuationItemStatus.New
            });
        }

        var currentIds = current.Select(x => x.Occurrence.Id).ToHashSet();
        foreach (var removed in manifest.Occurrences.Where(x => !currentIds.Contains(x.OccurrenceId)))
        {
            items.Add(new ReportContinuationItem
            {
                OccurrenceId = removed.OccurrenceId,
                LocationId = removed.LocationId,
                SectionId = removed.SectionId,
                Description = "Registro não encontrado no projeto atual",
                Status = ReportContinuationItemStatus.Removed,
                PreviousHash = removed.OccurrenceHash
            });
        }

        if (items.Any(x => x.Status == ReportContinuationItemStatus.AssociationError))
            errors.Add("Há evidências associadas a uma anomalia diferente da ocorrência atual.");
        var locationsFound = project.Locations.Count(x => ContainsNormalized(documentText, x.Name));
        var selectedSections = ReportValidation.OrderedSections(project, report).ToList();
        var sectionsFound = selectedSections.Count(x => ContainsNormalized(documentText, x.Section.Name));
        var nextVersion = Math.Max(manifest.ReportVersion + 1, ParseRevision(fullPath) + 1);
        var suggested = GetRevisionOutputPath(fullPath, nextVersion);
        nextVersion = ParseRevision(suggested);
        return new ReportContinuationAnalysis
        {
            SourcePath = fullPath,
            SourceHash = sourceHash,
            HasManifest = hasManifest,
            RequiresManualReview = requiresReview,
            PageCount = pageCount,
            LocationCount = locationsFound,
            SectionCount = sectionsFound,
            ExistingCount = items.Count(x => x.Status == ReportContinuationItemStatus.Existing),
            NewCount = items.Count(x => x.Status == ReportContinuationItemStatus.New),
            ModifiedCount = items.Count(x => x.Status == ReportContinuationItemStatus.Modified),
            RemovedCount = items.Count(x => x.Status == ReportContinuationItemStatus.Removed),
            MissingImageCount = items.Sum(x => x.MissingImageCount),
            LastFigureNumber = lastFigure,
            NextVersion = nextVersion,
            SuggestedOutputPath = suggested,
            Errors = errors,
            Warnings = warnings.Distinct().ToList(),
            Items = items,
            Manifest = manifest
        };
    }

    private ReportContinuationResult ContinueReport(Project project, Report report, string sourcePath,
        IEnumerable<Guid> selectedOccurrenceIds, bool confirmLegacyReview, CancellationToken cancellationToken)
    {
        var analysis = AnalyzeContinuation(project, report, sourcePath, cancellationToken);
        if (analysis.Errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, analysis.Errors));
        if (analysis.RequiresManualReview && !confirmLegacyReview)
            throw new InvalidOperationException("Revise e confirme as correspondências do relatório antigo antes de continuar.");
        var selectedIds = selectedOccurrenceIds.Distinct().ToHashSet();
        var allowed = analysis.Items.Where(x => x.CanSelect).Select(x => x.OccurrenceId).ToHashSet();
        selectedIds.IntersectWith(allowed);
        if (selectedIds.Count == 0)
            throw new InvalidOperationException("Nenhuma anomalia nova ou alterada foi selecionada.");

        var selected = EnumerateReportOccurrences(project, report)
            .Where(x => selectedIds.Contains(x.Occurrence.Id)).ToList();
        var outputPath = GetRevisionOutputPath(analysis.SourcePath, analysis.NextVersion);
        var outputVersion = ParseRevision(outputPath);
        Directory.CreateDirectory(_paths.CacheDirectory);
        var temporaryPath = Path.Combine(_paths.CacheDirectory,
            $"report-continuation-{Guid.NewGuid():N}.tmp.docx");
        var originalHash = analysis.SourceHash;
        var exchangeDocument = _inspectionExchangeService.Build(project, report);
        try
        {
            CopyFileWithSharedRead(analysis.SourcePath, temporaryPath);
            var manifest = analysis.Manifest;
            var figureChapter = 4;
            var figureNumber = 1;
            var addedEvidences = 0;
            using (var document = WordprocessingDocument.Open(temporaryPath, true))
            {
                var mainPart = document.MainDocumentPart ?? throw new InvalidDataException("DOCX sem documento principal.");
                var body = mainPart.Document.Body ?? throw new InvalidDataException("DOCX sem corpo principal.");
                var existingSequence = NormalizeFigureSequences(body);
                figureChapter = existingSequence.Chapter;
                figureNumber = existingSequence.LastNumber + 1;
                EnsureContinuationBookmark(body);
                var anchor = FindContinuationAnchor(body)
                    ?? throw new InvalidDataException("Marcador de continuação não encontrado.");
                var insertionParent = anchor.Parent as OpenXmlCompositeElement
                    ?? throw new InvalidDataException("O marcador de continuação não possui contêiner válido.");
                var templates = FindContinuationTemplates(body, project, figureChapter);
                var elements = new List<OpenXmlElement>();

                foreach (var locationGroup in selected.GroupBy(x => x.Location.Id))
                {
                    var location = (Paragraph)templates.Location.CloneNode(true);
                    SetParagraphText(location, locationGroup.First().Location.Name.Trim().ToUpperInvariant());
                    ConfigureParagraph(location, keepNext: true, before: 240);
                    elements.Add(location);

                    foreach (var sectionGroup in locationGroup.GroupBy(x => x.Section.Id))
                    {
                        var sectionData = sectionGroup.First().Section;
                        var section = (Paragraph)templates.Section.CloneNode(true);
                        SetParagraphText(section, FormatSectionHeading(sectionData));
                        ConfigureParagraph(section, keepNext: true, before: 240, after: 120);
                        elements.Add(section);
                        if (!string.IsNullOrWhiteSpace(sectionData.Description))
                        {
                            var description = (Paragraph)templates.Body.CloneNode(true);
                            SetParagraphText(description, sectionData.Description);
                            ConfigureParagraph(description, keepNext: true, after: 120);
                            elements.Add(description);
                        }

                        var imageElements = new List<OpenXmlElement>();
                        foreach (var occurrenceData in sectionGroup.OrderBy(x => x.Occurrence.Order)
                                     .ThenBy(x => x.Occurrence.CreatedAt))
                        {
                            var occurrence = occurrenceData.Occurrence;
                            var evidences = occurrence.Evidence.OrderBy(x => x.Order)
                                .Where(x => IsSupportedImageFile(x.FilePath)).ToList();
                            var firstFigure = figureNumber;
                            var reference = evidences.Count switch
                            {
                                0 => string.Empty,
                                1 => $" (Figura {figureChapter}.{firstFigure})",
                                _ => $" (Figuras {figureChapter}.{firstFigure} a {figureChapter}.{firstFigure + evidences.Count - 1})"
                            };
                            var occurrenceParagraph = (Paragraph)templates.Occurrence.CloneNode(true);
                            SetOccurrenceParagraphText(occurrenceParagraph, FormatOccurrenceForReport(occurrence),
                                reference, occurrence.IsSevere);
                            ConfigureParagraph(occurrenceParagraph, keepLines: true, after: 80);
                            elements.Add(occurrenceParagraph);

                            var export = new ReportExportOccurrence
                            {
                                LocationId = occurrenceData.Location.Id,
                                SectionId = occurrenceData.Section.Id,
                                OccurrenceId = occurrence.Id,
                                OccurrenceHash = ComputeOccurrenceHash(occurrence)
                            };
                            foreach (var evidence in evidences)
                            {
                                imageElements.Add(CloneImageParagraph(mainPart, templates.Image, evidence.FilePath));
                                var caption = (Paragraph)templates.Caption.CloneNode(true);
                                var captionText = NormalizeReportDescription(OccurrenceRules.GenerateCaptionSummary(occurrence));
                                SetParagraphText(caption,
                                    $"Figura {figureChapter}.{figureNumber} – {captionText.TrimEnd('.', ';')}.");
                                ConfigureParagraph(caption, keepLines: true, center: true, after: 240);
                                imageElements.Add(caption);
                                export.Evidence.Add(new ReportExportEvidence
                                {
                                    EvidenceId = evidence.Id,
                                    EvidenceHash = ComputeFileHash(evidence.FilePath),
                                    FigureNumber = figureNumber
                                });
                                figureNumber++;
                                addedEvidences++;
                            }
                            manifest.Occurrences.RemoveAll(x => x.OccurrenceId == occurrence.Id);
                            manifest.Occurrences.Add(export);
                        }
                        elements.AddRange(imageElements);
                    }
                }

                foreach (var element in elements) insertionParent.InsertBefore(element, anchor);
                manifest.ProjectId = project.Id;
                manifest.ReportId = report.Id;
                manifest.ReportVersion = outputVersion;
                manifest.BaseDocumentHash = originalHash;
                manifest.UpdatedAtUtc = DateTime.UtcNow;
                manifest.LastFigureNumber = figureNumber - 1;
                WriteContinuationManifest(mainPart, manifest);
                _inspectionExchangeService.WriteInspectionManifest(mainPart, exchangeDocument);
                MarkFieldsForUpdate(mainPart);
                mainPart.Document.Save();
            }

            using (var verification = WordprocessingDocument.Open(temporaryPath, false))
                if (verification.MainDocumentPart?.Document.Body is null ||
                    ReadContinuationManifest(verification.MainDocumentPart) is null)
                    throw new InvalidDataException("A versão atualizada não pôde ser validada.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(ComputeFileHash(analysis.SourcePath), originalHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("O relatório base foi alterado durante a atualização.");
            File.Move(temporaryPath, outputPath, false);
            try
            {
                var sidecar = new FileInspectionTransport(FileInspectionTransport.GetSidecarPath(outputPath),
                    _inspectionExchangeService.Serializer);
                var result = sidecar.SendAsync(exchangeDocument, cancellationToken).GetAwaiter().GetResult();
                if (result.Status == InspectionTransportStatus.Cancelled)
                    throw new OperationCanceledException(cancellationToken);
                if (!result.Success)
                    throw new IOException(result.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InspectionExchangeExportException(outputPath, ex);
            }
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            _logger.Info($"Continuação do relatório {report.Id}: R{outputVersion:00}, " +
                         $"{selected.Count} anomalia(s), {addedEvidences} evidência(s), {outputPath}");
            return new ReportContinuationResult
            {
                BaseFilePath = analysis.SourcePath,
                OutputPath = outputPath,
                BaseFileHash = originalHash,
                Version = outputVersion,
                AddedOccurrences = selected.Count,
                AddedEvidences = addedEvidences,
                ManifestJson = manifestJson
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao continuar relatório {report.Id}.", ex);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ValidateDocxPackage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Selecione um relatório DOCX.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Somente arquivos .docx sem macros são aceitos.");
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("O relatório selecionado não foi encontrado.", fullPath);
        if (info.Length == 0 || info.Length > MaxDocxBytes)
            throw new InvalidDataException("O tamanho do DOCX é inválido ou excede o limite de 512 MB.");
        using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != 4 || signature[0] != (byte)'P' || signature[1] != (byte)'K')
            throw new InvalidDataException("O arquivo não é um pacote OpenXML válido ou está protegido por senha.");
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            if (zip.Entries.Count > MaxZipEntries) throw new InvalidDataException("O DOCX possui entradas demais.");
            long expanded = 0;
            var images = 0;
            foreach (var entry in zip.Entries)
            {
                if (Path.IsPathRooted(entry.FullName) || entry.FullName.Split('/').Contains(".."))
                    throw new InvalidDataException("O pacote contém caminho inseguro.");
                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Documentos com macros não são aceitos.");
                expanded = checked(expanded + entry.Length);
                if (expanded > MaxExpandedBytes) throw new InvalidDataException("O conteúdo expandido do DOCX excede 1 GB.");
                if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 1000)
                    throw new InvalidDataException("O pacote apresenta taxa de compressão insegura.");
                if (entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)) images++;
            }
            if (images > MaxImages) throw new InvalidDataException($"O DOCX excede o limite de {MaxImages} imagens.");
        }
        try
        {
            using var packageStream = File.Open(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = WordprocessingDocument.Open(packageStream, false);
            if (document.MainDocumentPart?.Document.Body is null)
                throw new InvalidDataException("DOCX sem corpo principal.");
        }
        catch (OpenXmlPackageException ex)
        {
            throw new InvalidDataException("O DOCX está corrompido ou é incompatível.", ex);
        }
        return fullPath;
    }

    private static ReportExportManifest BuildGeneratedManifest(Project project, Report report)
    {
        var manifest = new ReportExportManifest { ProjectId = project.Id, ReportId = report.Id };
        var figure = 1;
        foreach (var item in EnumerateReportOccurrences(project, report))
        {
            var occurrence = new ReportExportOccurrence
            {
                LocationId = item.Location.Id,
                SectionId = item.Section.Id,
                OccurrenceId = item.Occurrence.Id,
                OccurrenceHash = ComputeOccurrenceHash(item.Occurrence)
            };
            foreach (var evidence in item.Occurrence.Evidence.OrderBy(x => x.Order)
                         .Where(x => IsSupportedImageFile(x.FilePath)))
            {
                occurrence.Evidence.Add(new ReportExportEvidence
                {
                    EvidenceId = evidence.Id,
                    EvidenceHash = ComputeFileHash(evidence.FilePath),
                    FigureNumber = figure++
                });
            }
            manifest.Occurrences.Add(occurrence);
        }
        var customImages = report.CustomSections.SelectMany(x => x.Images)
            .Count(x => IsSupportedImageFile(x.FilePath));
        manifest.LastFigureNumber = figure - 1 + customImages;
        return manifest;
    }

    private static ReportExportManifest BuildLegacyManifest(Project project, Report report, string documentText,
        int lastFigure, List<string> warnings, ref bool requiresReview)
    {
        var manifest = new ReportExportManifest
        {
            ProjectId = project.Id,
            ReportId = report.Id,
            LastFigureNumber = lastFigure,
            ReportVersion = ParseRevisionFromName(Path.GetFileNameWithoutExtension(report.GeneratedReportPath ?? string.Empty))
        };
        var all = EnumerateReportOccurrences(project, report).ToList();
        var sectionBlocks = all.Select(x => x.Section).DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => FindSectionDocumentBlock(documentText, x, all.Select(y => y.Section)));
        var duplicateDescriptions = all.GroupBy(x => (x.Section.Id,
                Description: NormalizeForMatch(FormatOccurrenceForReport(x.Occurrence))))
            .Where(x => !string.IsNullOrWhiteSpace(x.Key.Description) && x.Count() > 1)
            .Select(x => x.Key).ToHashSet();
        foreach (var item in all)
        {
            var description = NormalizeForMatch(FormatOccurrenceForReport(item.Occurrence));
            var sectionText = sectionBlocks.GetValueOrDefault(item.Section.Id) ?? string.Empty;
            if (description.Length == 0 || !NormalizeForMatch(sectionText).Contains(description, StringComparison.Ordinal))
                continue;
            if (duplicateDescriptions.Contains((item.Section.Id, description)))
            {
                requiresReview = true;
                warnings.Add($"Correspondência ambígua em '{item.Section.Name}': descrições repetidas.");
                continue;
            }
            var exported = new ReportExportOccurrence
            {
                LocationId = item.Location.Id,
                SectionId = item.Section.Id,
                OccurrenceId = item.Occurrence.Id,
                OccurrenceHash = ComputeOccurrenceHash(item.Occurrence)
            };
            var referencedFigures = DetectOccurrenceFigureNumbers(sectionText, item.Occurrence);
            var evidences = item.Occurrence.Evidence.OrderBy(x => x.Order)
                .Where(x => IsSupportedImageFile(x.FilePath)).ToList();
            for (var index = 0; index < Math.Min(evidences.Count, referencedFigures.Count); index++)
                exported.Evidence.Add(new ReportExportEvidence
                {
                    EvidenceId = evidences[index].Id,
                    EvidenceHash = ComputeFileHash(evidences[index].FilePath),
                    FigureNumber = referencedFigures[index]
                });
            manifest.Occurrences.Add(exported);
        }
        if (lastFigure > 0 && manifest.Occurrences.Count == 0)
        {
            requiresReview = true;
            warnings.Add("Há figuras no relatório, mas nenhuma anomalia pôde ser associada com segurança.");
        }
        return manifest;
    }

    private static IEnumerable<(Location Location, InspectionSection Section, Occurrence Occurrence)>
        EnumerateReportOccurrences(Project project, Report report)
    {
        foreach (var (location, section) in ReportValidation.OrderedSections(project, report))
            foreach (var occurrence in section.Occurrences.OrderBy(x => x.Order).ThenBy(x => x.CreatedAt))
                yield return (location, section, occurrence);
    }

    private static string ComputeOccurrenceHash(Occurrence occurrence)
    {
        var payload = new
        {
            occurrence.Id,
            occurrence.LocationMode,
            occurrence.Meterage,
            occurrence.EndMeterage,
            MeteragePoints = occurrence.MeteragePoints.OrderBy(x => x.Order).Select(x => x.Meterage),
            occurrence.VideoTimestamp,
            occurrence.ReferencePoint,
            occurrence.Position,
            occurrence.Notes,
            occurrence.CustomGeneratedText,
            occurrence.GeneratedDescription,
            occurrence.ManualDescriptionOverride,
            occurrence.InterruptedRecording,
            occurrence.IsSevere,
            Anomalies = occurrence.Anomalies.OrderBy(x => x.Order).Select(x => new
            {
                x.AnomalyTypeId, x.AnomalyName, x.Position, x.Orientation, x.ApproximateExtension,
                x.Dimension, x.Condition, x.InfiltrationCondition, x.EstimatedPercentage, x.Notes, x.CustomText
            }),
            Evidence = occurrence.Evidence.OrderBy(x => x.Order).Select(x => new
            {
                x.Id, x.Caption, x.VideoTimestamp, Exists = File.Exists(x.FilePath),
                Length = File.Exists(x.FilePath) ? new FileInfo(x.FilePath).Length : -1,
                Updated = File.Exists(x.FilePath) ? File.GetLastWriteTimeUtc(x.FilePath).Ticks : 0
            })
        };
        return ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, ManifestJsonOptions)));
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CopyFileWithSharedRead(string sourcePath, string destinationPath)
    {
        using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var destination = File.Open(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
        destination.Flush(true);
    }

    private static string ComputeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteContinuationManifest(MainDocumentPart mainPart, ReportExportManifest manifest)
    {
        manifest.UpdatedAtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        var xml = new XDocument(new XElement(XName.Get("manifest", ManifestNamespace), new XCData(json)));
        var part = FindManifestPart(mainPart) ?? mainPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using var stream = new MemoryStream();
        xml.Save(stream, SaveOptions.DisableFormatting);
        stream.Position = 0;
        part.FeedData(stream);
    }

    private static ReportExportManifest? ReadContinuationManifest(MainDocumentPart mainPart)
    {
        var part = FindManifestPart(mainPart);
        if (part is null) return null;
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        var xml = XDocument.Load(stream);
        var json = xml.Root?.Value;
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<ReportExportManifest>(json, ManifestJsonOptions);
    }

    private static CustomXmlPart? FindManifestPart(MainDocumentPart mainPart)
    {
        foreach (var part in mainPart.CustomXmlParts)
        {
            try
            {
                using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
                var xml = XDocument.Load(stream);
                if (xml.Root?.Name.NamespaceName == ManifestNamespace) return part;
            }
            catch (Exception) when (part is not null) { }
        }
        return null;
    }

    private static void EnsureContinuationBookmark(Body body)
    {
        if (FindContinuationAnchor(body) is not null) return;
        var signature = FindSignatureStart(body)
            ?? throw new InvalidDataException("A assinatura do relatório não foi localizada.");
        var maxId = body.Descendants<BookmarkStart>().Select(x =>
                int.TryParse(x.Id?.Value, out var id) ? id : 0).DefaultIfEmpty().Max() + 1;
        signature.PrependChild(new BookmarkEnd { Id = maxId.ToString(CultureInfo.InvariantCulture) });
        signature.PrependChild(new BookmarkStart
        {
            Id = maxId.ToString(CultureInfo.InvariantCulture), Name = ContinuationBookmarkName
        });
    }

    private static Paragraph? FindContinuationAnchor(Body body) => body.Descendants<BookmarkStart>()
        .FirstOrDefault(x => x.Name?.Value == ContinuationBookmarkName)?.Ancestors<Paragraph>().FirstOrDefault();

    private static Paragraph? FindSignatureStart(Body body)
    {
        var name = body.Descendants<Paragraph>().LastOrDefault(x =>
            Regex.IsMatch(ParagraphText(x), @"Felipe\s+Fran[cç]a", RegexOptions.IgnoreCase));
        if (name is null) return null;
        var candidate = name;
        var previous = name.PreviousSibling<Paragraph>();
        for (var count = 0; previous is not null && count < 4;
             previous = previous.PreviousSibling<Paragraph>(), count++)
        {
            var text = ParagraphText(previous).Trim();
            if (previous.Descendants<Drawing>().Any()) candidate = previous;
            else if (text.Length > 0) break;
        }
        return candidate;
    }

    private static bool HasContinuationTemplates(Body body, Project project, int figureChapter)
    {
        try { _ = FindContinuationTemplates(body, project, figureChapter); return true; }
        catch (InvalidDataException) { return false; }
    }

    private static ContinuationTemplates FindContinuationTemplates(Body body, Project project, int figureChapter)
    {
        var paragraphs = body.Descendants<Paragraph>().ToList();
        var location = paragraphs.FirstOrDefault(p => project.Locations.Any(l =>
                           NormalizeForMatch(ParagraphText(p)) == NormalizeForMatch(l.Name)))
                       ?? paragraphs.FirstOrDefault(p => ParagraphText(p).Trim().Length > 0 &&
                           p.NextSibling<Paragraph>() is { } next &&
                           ParagraphText(next).TrimStart().StartsWith("TRECHO", StringComparison.OrdinalIgnoreCase));
        var section = paragraphs.FirstOrDefault(p =>
            ParagraphText(p).TrimStart().StartsWith("TRECHO", StringComparison.OrdinalIgnoreCase));
        var occurrence = paragraphs.FirstOrDefault(p => ParagraphText(p).TrimStart().StartsWith("- "));
        var caption = paragraphs.FirstOrDefault(p =>
            Regex.IsMatch(ParagraphText(p).TrimStart(),
                $@"^Figura\s+{figureChapter.ToString(CultureInfo.InvariantCulture)}\.\d+", RegexOptions.IgnoreCase));
        var image = caption?.PreviousSibling<Paragraph>();
        while (image is not null && !image.Descendants<Drawing>().Any()) image = image.PreviousSibling<Paragraph>();
        var bodyTemplate = paragraphs.FirstOrDefault(p => project.Locations.SelectMany(x => x.Sections)
                               .Any(s => !string.IsNullOrWhiteSpace(s.Description) &&
                                   NormalizeForMatch(ParagraphText(p)) == NormalizeForMatch(s.Description!)))
                           ?? paragraphs.FirstOrDefault(p => ParagraphText(p).TrimStart()
                               .StartsWith("A seguir", StringComparison.OrdinalIgnoreCase));
        if (location is null || section is null || occurrence is null || caption is null || image is null ||
            bodyTemplate is null)
            throw new InvalidDataException("Blocos de estilo necessários para continuar o relatório não encontrados.");
        return new ContinuationTemplates(location, section, occurrence, caption, image, bodyTemplate);
    }

    private sealed record ContinuationTemplates(Paragraph Location, Paragraph Section, Paragraph Occurrence,
        Paragraph Caption, Paragraph Image, Paragraph Body);

    private static int DetectLastFigureNumber(Body body)
    {
        return DetectFigureSequence(body).LastNumber;
    }

    private static int DetectLastFigureNumber(string text)
    {
        var matches = Regex.Matches(text, @"Figura(?:s)?\s+(?<chapter>\d+)\.(?<number>\d+)",
            RegexOptions.IgnoreCase).Cast<Match>().ToList();
        if (matches.Count == 0) return 0;
        var chapter = int.Parse(matches[^1].Groups["chapter"].Value, CultureInfo.InvariantCulture);
        return matches.Where(x => int.Parse(x.Groups["chapter"].Value, CultureInfo.InvariantCulture) == chapter)
            .Max(x => int.Parse(x.Groups["number"].Value, CultureInfo.InvariantCulture));
    }

    private static FigureSequence DetectFigureSequence(Body body)
    {
        var captions = body.Descendants<Paragraph>().Select(ParagraphText)
            .Select(x => Regex.Match(x, @"^\s*Figura\s+(?<chapter>\d+)\.(?<number>\d+)",
                RegexOptions.IgnoreCase)).Where(x => x.Success).ToList();
        if (captions.Count == 0) return new FigureSequence(4, 0);
        var chapter = int.Parse(captions[^1].Groups["chapter"].Value, CultureInfo.InvariantCulture);
        var lastNumber = captions.Count(x =>
            int.Parse(x.Groups["chapter"].Value, CultureInfo.InvariantCulture) == chapter);
        return new FigureSequence(chapter, lastNumber);
    }

    private static FigureSequence NormalizeFigureSequences(Body body)
    {
        var paragraphs = body.Descendants<Paragraph>().ToList();
        var captions = paragraphs.Select((paragraph, index) =>
            {
                var match = Regex.Match(ParagraphText(paragraph),
                    @"^\s*Figura\s+(?<chapter>\d+)\.(?<number>\d+)", RegexOptions.IgnoreCase);
                return match.Success
                    ? (Index: index,
                        Chapter: int.Parse(match.Groups["chapter"].Value, CultureInfo.InvariantCulture),
                        Number: int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture))
                    : (Index: -1, Chapter: 0, Number: 0);
            }).Where(x => x.Index >= 0).ToList();
        if (captions.Count == 0) return new FigureSequence(4, 0);

        var groups = new List<List<(int Index, int Chapter, int Number)>>();
        foreach (var caption in captions)
        {
            if (groups.Count == 0 || caption.Chapter != groups[^1][^1].Chapter ||
                caption.Number <= groups[^1][^1].Number)
                groups.Add([]);
            groups[^1].Add(caption);
        }

        var nextByChapter = new Dictionary<int, int>();
        var paragraphStart = 0;
        foreach (var group in groups)
        {
            var replacements = new Dictionary<(int Chapter, int Number), int>();
            foreach (var caption in group)
            {
                var next = nextByChapter.GetValueOrDefault(caption.Chapter) + 1;
                nextByChapter[caption.Chapter] = next;
                replacements[(caption.Chapter, caption.Number)] = next;
            }
            var paragraphEnd = group[^1].Index;
            for (var index = paragraphStart; index <= paragraphEnd; index++)
                ReplaceFigureNumbers(paragraphs[index], replacements);
            paragraphStart = paragraphEnd + 1;
        }

        var chapter = captions[^1].Chapter;
        return new FigureSequence(chapter, nextByChapter[chapter]);
    }

    private static void ReplaceFigureNumbers(Paragraph paragraph,
        IReadOnlyDictionary<(int Chapter, int Number), int> replacements)
    {
        var original = ParagraphText(paragraph);
        if (!original.Contains("Figura", StringComparison.OrdinalIgnoreCase)) return;
        var updated = ReplaceFigureTokens(original, replacements);
        if (updated == original) return;

        var texts = paragraph.Descendants<Text>().ToList();
        var perNode = texts.Select(x => ReplaceFigureTokens(x.Text, replacements)).ToList();
        if (string.Concat(perNode) == updated)
        {
            for (var index = 0; index < texts.Count; index++)
            {
                texts[index].Text = perNode[index];
                texts[index].Space = SpaceProcessingModeValues.Preserve;
            }
            return;
        }

        texts[0].Text = updated;
        texts[0].Space = SpaceProcessingModeValues.Preserve;
        foreach (var text in texts.Skip(1)) text.Text = string.Empty;
    }

    private static string ReplaceFigureTokens(string text,
        IReadOnlyDictionary<(int Chapter, int Number), int> replacements)
    {
        text = Regex.Replace(text,
            @"(?<label>\bFiguras\s+)(?<chapter1>\d+)\.(?<first>\d+)" +
            @"(?<between>\s+(?:a|até|e)\s+)(?<chapter2>\d+)\.(?<last>\d+)",
            match => $"{match.Groups["label"].Value}{ReplaceFigureToken(match, "chapter1", "first", replacements)}" +
                     $"{match.Groups["between"].Value}{ReplaceFigureToken(match, "chapter2", "last", replacements)}",
            RegexOptions.IgnoreCase);
        return Regex.Replace(text,
            @"(?<label>\bFigura\s+)(?<chapter>\d+)\.(?<number>\d+)",
            match => $"{match.Groups["label"].Value}{ReplaceFigureToken(match, "chapter", "number", replacements)}",
            RegexOptions.IgnoreCase);
    }

    private static string ReplaceFigureToken(Match match, string chapterGroup, string numberGroup,
        IReadOnlyDictionary<(int Chapter, int Number), int> replacements)
    {
        var chapter = int.Parse(match.Groups[chapterGroup].Value, CultureInfo.InvariantCulture);
        var number = int.Parse(match.Groups[numberGroup].Value, CultureInfo.InvariantCulture);
        return $"{chapter}.{replacements.GetValueOrDefault((chapter, number), number)}";
    }

    private sealed record FigureSequence(int Chapter, int LastNumber);

    private static string FindSectionDocumentBlock(string documentText, InspectionSection section,
        IEnumerable<InspectionSection> reportSections)
    {
        var lines = documentText.Split('\n');
        var target = CompactForMatch(FormatSectionHeading(section));
        var headings = reportSections.Select(FormatSectionHeading).Select(CompactForMatch).ToHashSet();
        var start = Array.FindIndex(lines, x => CompactForMatch(x) == target);
        if (start < 0) return string.Empty;
        var end = lines.Length;
        for (var index = start + 1; index < lines.Length; index++)
            if (headings.Contains(CompactForMatch(lines[index])))
            {
                end = index;
                break;
            }
        return string.Join("\n", lines[start..end]);
    }

    private static string CompactForMatch(string value) =>
        NormalizeForMatch(value).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static List<int> DetectOccurrenceFigureNumbers(string documentText, Occurrence occurrence)
    {
        var description = NormalizeForMatch(FormatOccurrenceForReport(occurrence));
        if (description.Length == 0) return [];
        foreach (var line in documentText.Split('\n'))
        {
            if (!line.Contains("Figura", StringComparison.OrdinalIgnoreCase) ||
                !NormalizeForMatch(line).Contains(description, StringComparison.Ordinal)) continue;
            var range = Regex.Match(line, @"Figuras\s+\d+\.(\d+)\s+a\s+\d+\.(\d+)", RegexOptions.IgnoreCase);
            if (range.Success && int.TryParse(range.Groups[1].Value, out var first) &&
                int.TryParse(range.Groups[2].Value, out var last) && last >= first)
                return Enumerable.Range(first, last - first + 1).ToList();
            return Regex.Matches(line, @"Figura\s+\d+\.(\d+)", RegexOptions.IgnoreCase)
                .Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        }
        return [];
    }

    private static int ReadStoredPageCount(WordprocessingDocument document)
    {
        var value = document.ExtendedFilePropertiesPart?.Properties?.ChildElements
            .FirstOrDefault(x => x.LocalName == "Pages")?.InnerText;
        return int.TryParse(value, out var pages) ? pages : 0;
    }

    private static string GetRevisionOutputPath(string sourcePath, int minimumVersion)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var baseName = Regex.Replace(Path.GetFileNameWithoutExtension(sourcePath), @"-R\d+$", "",
            RegexOptions.IgnoreCase);
        for (var version = Math.Max(1, minimumVersion); ; version++)
        {
            var candidate = Path.Combine(directory, $"{baseName}-R{version:00}.docx");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static int ParseRevision(string path) => ParseRevisionFromName(Path.GetFileNameWithoutExtension(path));

    private static int ParseRevisionFromName(string name)
    {
        var match = Regex.Match(name, @"-R(\d+)$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var revision) ? revision : 0;
    }

    private static bool ContainsNormalized(string source, string value) =>
        !string.IsNullOrWhiteSpace(value) && NormalizeForMatch(source).Contains(NormalizeForMatch(value),
            StringComparison.Ordinal);

    private static string NormalizeForMatch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
}
