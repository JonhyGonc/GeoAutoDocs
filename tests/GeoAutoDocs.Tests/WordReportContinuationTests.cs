using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace GeoAutoDocs.Tests;

public sealed class WordReportContinuationTests : IAsyncLifetime
{
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nEkAAAAASUVORK5CYII=";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsContinuationTests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private AppDataPaths _paths = null!;
    private ProjectStore _store = null!;
    private string _image = null!;

    public WordReportContinuationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(_root);
        _store = new ProjectStore(_paths);
        await _store.InitializeAsync();
        _image = Path.Combine(_root, "evidence.png");
        await File.WriteAllBytesAsync(_image, Convert.FromBase64String(TinyPng));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ManifestContinuationPreservesManualContentVersionsFiguresAndPersistence()
    {
        var (project, report, section) = await CreateReportAsync();
        var first = AddOccurrence(section, "Anomalia inicial", _image);
        var service = new WordReportService(_paths);
        var basePath = await service.GenerateToAsync(project, report, Path.Combine(_root, "relatorio.docx"));
        AddManualParagraph(basePath, "CONTEÚDO MANUAL PRESERVADO");
        var baseHash = Hash(basePath);

        var initial = await service.AnalyzeContinuationAsync(project, report, basePath);
        Assert.True(initial.HasManifest);
        Assert.Equal(1, initial.ExistingCount);
        Assert.Equal(0, initial.NewCount);
        Assert.Equal(1, initial.LastFigureNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ContinueReportAsync(project, report, basePath, [], false));

        var second = AddOccurrence(section, "Nova anomalia", _image, _image);
        var preview = await service.AnalyzeContinuationAsync(project, report, basePath);
        Assert.Equal(1, preview.NewCount);
        Assert.True(preview.Items.Single(x => x.OccurrenceId == second.Id).IsSelected);
        var r01 = await service.ContinueReportAsync(project, report, basePath, [second.Id], false);

        Assert.EndsWith("-R01.docx", r01.OutputPath);
        Assert.Equal(baseHash, Hash(basePath));
        var r01Text = ReadBody(r01.OutputPath);
        Assert.Contains("CONTEÚDO MANUAL PRESERVADO", r01Text);
        Assert.Equal(1, Count(r01Text, "Nova anomalia"));
        Assert.Contains("Figura 4.3", r01Text);
        Assert.True(HasImageFrameAndShadow(r01.OutputPath, "Figura 4.1"));
        Assert.True(HasImageFrameAndShadow(r01.OutputPath, "Figura 4.2"));
        Assert.Equal((4320000L, 3240000L), ReadImageExtent(r01.OutputPath, "Figura 4.2"));
        Assert.True(r01Text.IndexOf("Nova anomalia", StringComparison.Ordinal) <
                    r01Text.IndexOf("Felipe França", StringComparison.Ordinal));

        var noChanges = await service.AnalyzeContinuationAsync(project, report, r01.OutputPath);
        Assert.Equal(2, noChanges.ExistingCount);
        Assert.Equal(0, noChanges.NewCount);
        Assert.Equal(0, noChanges.ModifiedCount);

        first.CustomGeneratedText = "Anomalia inicial revisada";
        var third = AddOccurrence(section, "Anomalia sem imagem", Path.Combine(_root, "missing.png"));
        var changed = await service.AnalyzeContinuationAsync(project, report, r01.OutputPath);
        Assert.Equal(1, changed.ModifiedCount);
        Assert.Equal(1, changed.NewCount);
        Assert.Equal(1, changed.MissingImageCount);
        Assert.False(changed.Items.Single(x => x.OccurrenceId == first.Id).IsSelected);
        Assert.True(changed.Items.Single(x => x.OccurrenceId == third.Id).IsSelected);

        var r02 = await service.ContinueReportAsync(project, report, r01.OutputPath, [third.Id], false);
        Assert.EndsWith("-R02.docx", r02.OutputPath);
        Assert.DoesNotContain("Anomalia inicial revisada", ReadBody(r02.OutputPath));
        var stillModified = await service.AnalyzeContinuationAsync(project, report, r02.OutputPath);
        Assert.Equal(1, stillModified.ModifiedCount);
        var r03 = await service.ContinueReportAsync(project, report, r02.OutputPath, [first.Id], false);
        Assert.Contains("Anomalia inicial revisada", ReadBody(r03.OutputPath));
        Assert.Equal(3, (await service.AnalyzeContinuationAsync(project, report, r03.OutputPath)).ExistingCount);

        var reassignedReport = project.CreateReport("Relatório reassociado");
        reassignedReport.SelectSection(section);
        var reassignedOccurrence = AddOccurrence(section, "Anomalia após reassociação", _image);
        var reassignment = await service.AnalyzeContinuationAsync(project, reassignedReport, r03.OutputPath);
        Assert.Empty(reassignment.Errors);
        Assert.True(reassignment.RequiresManualReview);
        Assert.Contains(reassignment.Warnings, x => x.Contains("outro relatório deste projeto"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ContinueReportAsync(
            project, reassignedReport, r03.OutputPath, [reassignedOccurrence.Id], false));
        var reassignedResult = await service.ContinueReportAsync(
            project, reassignedReport, r03.OutputPath, [reassignedOccurrence.Id], true);
        var reassignedAnalysis = await service.AnalyzeContinuationAsync(
            project, reassignedReport, reassignedResult.OutputPath);
        Assert.Empty(reassignedAnalysis.Errors);
        Assert.False(reassignedAnalysis.RequiresManualReview);

        report.ContinuationExports.Add(new ReportContinuationExport
        {
            ReportId = report.Id, BaseFilePath = r02.BaseFilePath, BaseFileHash = r02.BaseFileHash,
            GeneratedFilePath = r02.OutputPath, Version = r02.Version, ManifestJson = r02.ManifestJson,
            GeneratedAt = DateTime.UtcNow, Status = "Concluído"
        });
        await _store.SaveAsync(project);
        var recovered = await _store.GetAsync(project.Id);
        Assert.Equal(r02.OutputPath, Assert.Single(recovered!.Reports.Single(x => x.Id == report.Id)
            .ContinuationExports).GeneratedFilePath);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp.docx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LegacyHeuristicRequiresConfirmationAndInvalidPackagesAreRejectedWithoutChangingSource()
    {
        var (project, report, section) = await CreateReportAsync();
        AddOccurrence(section, "Anomalia legada", _image);
        var service = new WordReportService(_paths);
        var legacy = await service.GenerateToAsync(project, report, Path.Combine(_root, "legado.docx"));
        RemoveManifestAndBookmark(legacy);
        var legacyHash = Hash(legacy);
        var added = AddOccurrence(section, "Nova em relatório antigo", _image);

        var preview = await service.AnalyzeContinuationAsync(project, report, legacy);
        Assert.False(preview.HasManifest);
        Assert.True(preview.RequiresManualReview);
        Assert.Equal(1, preview.ExistingCount);
        Assert.Equal(1, preview.NewCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ContinueReportAsync(project, report, legacy, [added.Id], false));
        var result = await service.ContinueReportAsync(project, report, legacy, [added.Id], true);
        Assert.Equal(legacyHash, Hash(legacy));
        Assert.Contains("Nova em relatório antigo", ReadBody(result.OutputPath));
        Assert.True((await service.AnalyzeContinuationAsync(project, report, result.OutputPath)).HasManifest);

        var corrupt = Path.Combine(_root, "corrompido.docx");
        await File.WriteAllTextAsync(corrupt, "não é um pacote ZIP");
        var corruptHash = Hash(corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AnalyzeContinuationAsync(project, report, corrupt));
        Assert.Equal(corruptHash, Hash(corrupt));
        var macro = Path.Combine(_root, "relatorio.docm");
        File.Copy(legacy, macro);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AnalyzeContinuationAsync(project, report, macro));
    }

    [Fact]
    public async Task ExternalContinuationNormalizesDuplicatesWithinCurrentFigureChapter()
    {
        var (project, report, section) = await CreateReportAsync();
        AddOccurrence(section, "Anomalia repetida", _image, _image);
        var service = new WordReportService(_paths);
        var basePath = await service.GenerateToAsync(project, report, Path.Combine(_root, "por-trecho.docx"));
        AddManualParagraph(basePath, "- Grupo externo (Figura 4.1).");
        AddManualParagraph(basePath, "Figura 4.1 – Reinício existente em outro trecho.");
        RemoveManifestAndBookmark(basePath);
        var sameSectionAdded = AddOccurrence(section, "Nova no trecho existente", _image);

        var location = project.Locations.Single();
        var nextSection = new InspectionSection { LocationId = location.Id, Name = "Trecho 02" };
        location.Sections.Add(nextSection);
        report.SelectSection(nextSection);
        var added = AddOccurrence(nextSection, "Anomalia repetida", _image);

        var analysis = await service.AnalyzeContinuationAsync(project, report, basePath);
        Assert.Equal(1, analysis.ExistingCount);
        Assert.Equal(2, analysis.NewCount);
        Assert.Equal(3, analysis.LastFigureNumber);
        var result = await service.ContinueReportAsync(project, report, basePath,
            [sameSectionAdded.Id, added.Id], true);
        var text = ReadBody(result.OutputPath);
        Assert.Contains("TRECHO 02", text);
        Assert.Contains("Grupo externo (Figura 4.3).", text);
        Assert.Contains("Nova no trecho existente (Figura 4.4).", text);
        Assert.Contains("Anomalia repetida (Figura 4.5).", text);
    }

    [Fact]
    public async Task GoldenMasterCanBeUpdatedWhenExplicitlyEnabled()
    {
        var golden = Environment.GetEnvironmentVariable("GEOAUTODOCS_GOLDEN_PATH");
        if (string.IsNullOrWhiteSpace(golden) || !File.Exists(golden)) return;
        var store = new ProjectStore(new AppDataPaths());
        await store.InitializeAsync();
        var service = new WordReportService(new AppDataPaths());
        var candidates = new List<(Project Project, Report Report, ReportContinuationAnalysis Analysis)>();
        foreach (var item in await store.ListAsync())
        {
            var project = await store.GetAsync(item.Id);
            if (project is null) continue;
            foreach (var report in project.Reports)
            {
                try
                {
                    var analysis = await service.AnalyzeContinuationAsync(project, report, golden);
                    candidates.Add((project, report, analysis));
                }
                catch (InvalidDataException) { }
            }
        }
        var selected = candidates.OrderByDescending(x =>
            x.Analysis.LocationCount * 1000 + x.Analysis.SectionCount * 100 +
            x.Analysis.ExistingCount * 10 + x.Analysis.NewCount).First();
        Assert.Empty(selected.Analysis.Errors);
        var ids = selected.Analysis.Items.Where(x => x.Status == ReportContinuationItemStatus.New)
            .Select(x => x.OccurrenceId).ToList();
        if (ids.Count == 0) ids = selected.Analysis.Items.Where(x => x.Status == ReportContinuationItemStatus.Modified)
            .Select(x => x.OccurrenceId).ToList();
        Assert.NotEmpty(ids);
        var result = await service.ContinueReportAsync(selected.Project, selected.Report, golden, ids, true);
        _output.WriteLine($"GOLDEN_OUTPUT={result.OutputPath}");
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(Hash(golden), result.BaseFileHash);
        Assert.Contains("Felipe França", ReadBody(result.OutputPath));
    }

    private async Task<(Project Project, Report Report, InspectionSection Section)> CreateReportAsync()
    {
        var project = await _store.CreateAsync("Projeto continuação");
        var report = project.CreateReport("Relatório");
        report.Client = "Cliente"; report.Cnpj = "00.000.000/0001-00"; report.Area = "Área";
        report.Contract = "CT"; report.PurchaseOrder = "PC"; report.ReportCode = "GF-RL-CONT-001";
        report.ReportDate = new DateTime(2026, 8, 18); report.TotalInvestigatedMeters = 10;
        report.Diameters = StandardDiameterSelection.Mm800;
        var location = new Location { ProjectId = project.Id, Name = "PV02" };
        var section = new InspectionSection
        {
            LocationId = location.Id, Name = "Trecho 01", StartPoint = "PV02", EndPoint = "PV01",
            Diameter = 800, Reference = "Subindo para esquerda", Description = "GI: 1.86"
        };
        location.Sections.Add(section); project.Locations.Add(location); report.SelectSection(section);
        await _store.SaveAsync(project);
        return (project, report, section);
    }

    private static Occurrence AddOccurrence(InspectionSection section, string description, params string[] images)
    {
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id, LocationMode = OccurrenceLocationMode.Point,
            Meterage = section.Occurrences.Count + 1, CustomGeneratedText = description,
            GeneratedDescription = description, Order = section.Occurrences.Count,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        foreach (var path in images)
            occurrence.Evidence.Add(new Evidence
            {
                OccurrenceId = occurrence.Id, FilePath = path, Order = occurrence.Evidence.Count,
                SourceType = EvidenceSourceType.ExternalImage
            });
        section.Occurrences.Add(occurrence);
        return occurrence;
    }

    private static void AddManualParagraph(string path, string text)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var body = document.MainDocumentPart!.Document.Body!;
        var signature = body.Descendants<Paragraph>().Last(x =>
            string.Concat(x.Descendants<Text>().Select(t => t.Text)).Contains("Felipe França"));
        signature.InsertBeforeSelf(new Paragraph(new Run(new Text(text))));
        document.MainDocumentPart.Document.Save();
    }

    private static void RemoveManifestAndBookmark(string path)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart!;
        var manifestParts = new List<CustomXmlPart>();
        foreach (var part in main.CustomXmlParts.ToList())
        {
            using var reader = new StreamReader(part.GetStream());
            if (reader.ReadToEnd().Contains("urn:geoautodocs:report-manifest:v1")) manifestParts.Add(part);
        }
        foreach (var part in manifestParts) main.DeletePart(part);
        var body = main.Document.Body!;
        var start = body.Descendants<BookmarkStart>().FirstOrDefault(x => x.Name == "GEOAUTODOCS_ANOMALIES_END");
        if (start is not null)
        {
            var id = start.Id?.Value;
            start.Remove();
            body.Descendants<BookmarkEnd>().FirstOrDefault(x => x.Id?.Value == id)?.Remove();
        }
        main.Document.Save();
    }

    private static string ReadBody(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return string.Join("\n", document.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Select(x => string.Concat(x.Descendants<Text>().Select(t => t.Text))));
    }

    private static bool HasImageFrameAndShadow(string path, string captionPrefix)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        var caption = paragraphs.First(x => string.Concat(x.Descendants<Text>().Select(t => t.Text))
            .StartsWith(captionPrefix, StringComparison.Ordinal));
        var index = paragraphs.IndexOf(caption);
        var pictureParagraph = paragraphs.Take(index).Last(x => x.Descendants<Drawing>().Any());
        return pictureParagraph.Descendants<DocumentFormat.OpenXml.Drawing.OuterShadow>().Any() &&
               pictureParagraph.Descendants<DocumentFormat.OpenXml.Drawing.Outline>().Any(x => x.Width?.Value > 0);
    }

    private static (long Width, long Height) ReadImageExtent(string path, string captionPrefix)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart!.Document.Body!.Descendants<Paragraph>().ToList();
        var caption = paragraphs.First(x => string.Concat(x.Descendants<Text>().Select(t => t.Text))
            .StartsWith(captionPrefix, StringComparison.Ordinal));
        var pictureParagraph = paragraphs.Take(paragraphs.IndexOf(caption)).Last(x => x.Descendants<Drawing>().Any());
        var extent = pictureParagraph.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().Single();
        return (extent.Cx!.Value, extent.Cy!.Value);
    }

    private static int Count(string value, string text) =>
        (value.Length - value.Replace(text, string.Empty, StringComparison.Ordinal).Length) / text.Length;

    private static string Hash(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
