using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace GeoAutoDocs.Tests;

public sealed class Stage5ReportTests : IDisposable
{
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nEkAAAAASUVORK5CYII=";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsStage5Tests", Guid.NewGuid().ToString("N"));
    private readonly AppDataPaths _paths;
    private readonly string _image;

    public Stage5ReportTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppDataPaths(_root);
        _paths.EnsureDirectories();
        _image = Path.Combine(_root, "evidence.png");
        File.WriteAllBytes(_image, Convert.FromBase64String(TinyPng));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void ValidationBlocksMissingFieldsSectionsAndEvidenceAndWarnsAboutPendingCapture()
    {
        var project = NewProject();
        var report = project.CreateReport();
        var service = new WordReportService(_paths);
        var empty = service.Validate(project, report);
        Assert.False(empty.CanGenerate);
        Assert.Contains(empty.Errors, x => x.Contains("Cliente"));
        Assert.Contains(empty.Errors, x => x.Contains("trecho"));

        Configure(report);
        var (_, section) = AddSection(project, "Local A", "Trecho 01", 0, 0);
        report.SelectSection(section);
        var occurrence = AddOccurrence(section, OccurrenceLocationMode.Point, 18m, null, ["Trinca"]);
        occurrence.Evidence.Add(new Evidence { OccurrenceId = occurrence.Id, FilePath = Path.Combine(_root, "missing.png") });
        report.LocationImagePath = Path.Combine(_root, "localizacao-ausente.png");
        project.Captures.Add(new GeoAutoDocs.Core.Capture
        {
            ProjectId = project.Id, SectionId = section.Id, Status = CaptureStatus.Pending,
            FilePath = _image, VideoReferenceId = Guid.NewGuid()
        });

        var result = service.Validate(project, report);
        Assert.False(result.CanGenerate);
        Assert.Contains(result.Errors, x => x.Contains("evidência não encontrada"));
        Assert.Contains(result.Errors, x => x.Contains("Imagem de localização não encontrada"));
        Assert.Contains(result.Warnings, x => x.Contains("captura(s) pendente(s)"));
    }

    [Fact]
    public async Task PartialSelectionGroupsLocationsAndUsesContinuousFiguresForMultiRangeEntireAndOverride()
    {
        var project = NewProject();
        var report = project.CreateReport("Relatório parcial");
        Configure(report);
        var localA = new Location { ProjectId = project.Id, Name = "LOCAL A", Order = 0 };
        var localB = new Location { ProjectId = project.Id, Name = "LOCAL B", Order = 1 };
        project.Locations.AddRange([localA, localB]);
        var sections = Enumerable.Range(1, 5).Select(index => new InspectionSection
        {
            LocationId = index <= 3 ? localA.Id : localB.Id,
            Name = $"Trecho {index:00}", Order = index,
            StartPoint = index == 4 ? "PV-04" : null, EndPoint = index == 4 ? "PV-05" : null,
            Diameter = 600 + index * 100
        }).ToList();
        sections[3].Reference = "Subindo para esquerda";
        sections[3].Description = "GI: 1.86";
        localA.Sections.AddRange(sections.Take(3)); localB.Sections.AddRange(sections.Skip(3));

        var multi = AddOccurrence(sections[1], OccurrenceLocationMode.Point, 18m, null, ["Trinca", "Infiltração"]);
        AddEvidence(multi, 2, "Multianomalia");
        var range = AddOccurrence(sections[1], OccurrenceLocationMode.Range, 18m, 24.5m, ["Assoreamento"]);
        AddEvidence(range, 1, "Intervalo");
        var entire = AddOccurrence(sections[3], OccurrenceLocationMode.EntireSection, null, null, ["Deformação"]);
        entire.ManualDescriptionOverride = "Descrição manual revisada para todo o trecho";
        AddEvidence(entire, 4, "Todo o trecho");
        report.SelectSection(sections[3]);
        report.SelectSection(sections[1]);

        var output = await new WordReportService(_paths).GenerateAsync(project, report);
        var text = ReadBody(output);

        Assert.Contains("LOCAL A", text);
        Assert.Contains("LOCAL B", text);
        Assert.Contains("TRECHO 02", text);
        Assert.Contains("PV-04 → PV-05", text);
        Assert.Contains("TRECHO 04 - PV-04 → PV-05 – REDE 1000 mm (Subindo para esquerda)", text);
        Assert.Contains("TRECHO 02 - REDE 800 mm", text);
        Assert.Contains("GI: 1.86", text);
        var paragraphs = ReadParagraphs(output);
        Assert.True(IndexOf(paragraphs, "LOCAL B") < IndexOf(paragraphs, "TRECHO 04 - PV-04 → PV-05 – REDE 1000 mm (Subindo para esquerda)"));
        Assert.True(IndexOf(paragraphs, "TRECHO 04 - PV-04 → PV-05 – REDE 1000 mm (Subindo para esquerda)") < IndexOf(paragraphs, "GI: 1.86"));
        Assert.DoesNotContain("TRECHO 01", text);
        Assert.DoesNotContain("TRECHO 03", text);
        Assert.DoesNotContain("TRECHO 05", text);
        Assert.Contains("Trinca e infiltração a 18 metros do início da filmagem", text);
        Assert.Contains("Assoreamento entre 18 e 24,5 metros do início da filmagem", text);
        Assert.Contains("Descrição manual revisada para todo o trecho", text);
        Assert.Contains("Figuras 4.1 a 4.2", text);
        Assert.Contains("Figura 4.3", text);
        Assert.Contains("Figuras 4.4 a 4.7", text);
        Assert.Single(Regex.Matches(text, "Figura 4\\.1"));
        Assert.Equal(2, Regex.Matches(text, "Figura 4\\.3").Count);
        Assert.Single(Regex.Matches(text, "Figura 4\\.7"));
        Assert.DoesNotContain("Figura 4.8", text);
        Assert.True(text.IndexOf("Através dos serviços executados", StringComparison.Ordinal) < text.IndexOf("LOCAL A", StringComparison.Ordinal));
        Assert.True(text.IndexOf("LOCAL B", StringComparison.Ordinal) < text.IndexOf("Felipe França", StringComparison.Ordinal));

        using var generated = WordprocessingDocument.Open(output, false);
        var evidenceCaptions = generated.MainDocumentPart!.Document.Body!.Elements<Paragraph>()
            .Where(x => string.Concat(x.Descendants<Text>().Select(t => t.Text)).StartsWith("Figura 4.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(7, evidenceCaptions.Count);
        foreach (var caption in evidenceCaptions)
        {
            var imageParagraph = caption.PreviousSibling<Paragraph>();
            var extent = Assert.Single(imageParagraph!.Descendants<DW.Extent>());
            Assert.True(extent.Cx!.Value <= 12L * 360000L);
            Assert.True(extent.Cy!.Value <= 9L * 360000L);
            Assert.True(extent.Cx.Value == 12L * 360000L || extent.Cy.Value == 9L * 360000L);
            Assert.True(imageParagraph.ParagraphProperties!.KeepNext!.Val!.Value);
            Assert.True(imageParagraph.ParagraphProperties.KeepLines!.Val!.Value);
            Assert.True(caption.ParagraphProperties!.KeepLines!.Val!.Value);
        }
    }

    [Fact]
    public async Task ReportsRemainIndependentAndEachFigureCounterStartsAtOne()
    {
        var project = NewProject();
        var first = project.CreateReport("001"); Configure(first, "GF-RL-001");
        var second = project.CreateReport("002"); Configure(second, "GF-RL-002");
        var location = new Location { ProjectId = project.Id, Name = "Local único" };
        project.Locations.Add(location);
        var a = new InspectionSection { LocationId = location.Id, Name = "Trecho A", Order = 0 };
        var b = new InspectionSection { LocationId = location.Id, Name = "Trecho B", Order = 1 };
        location.Sections.AddRange([a, b]);
        AddEvidence(AddOccurrence(a, OccurrenceLocationMode.Point, 1m, null, ["Fissura"]), 1, "A");
        AddEvidence(AddOccurrence(b, OccurrenceLocationMode.Point, 2m, null, ["Trinca"]), 1, "B");
        first.SelectSection(a); second.SelectSection(b);
        var service = new WordReportService(_paths);

        var firstOutput = await service.GenerateAsync(project, first);
        var secondOutput = await service.GenerateAsync(project, second);
        var firstText = ReadBody(firstOutput); var secondText = ReadBody(secondOutput);

        Assert.Contains("TRECHO A", firstText); Assert.DoesNotContain("TRECHO B", firstText);
        Assert.Contains("TRECHO B", secondText); Assert.DoesNotContain("TRECHO A", secondText);
        Assert.Contains("Figura 4.1", firstText); Assert.Contains("Figura 4.1", secondText);
        Assert.DoesNotContain("Figura 4.2", firstText); Assert.DoesNotContain("Figura 4.2", secondText);
    }

    [Fact]
    public async Task AtomicGenerationVersionsExistingDestinationWithoutOverwritingIt()
    {
        var project = NewProject(); var report = project.CreateReport(); Configure(report);
        var (_, section) = AddSection(project, "Local", "Trecho", 0, 0); report.SelectSection(section);
        var destination = Path.Combine(_root, "final.docx");
        await File.WriteAllTextAsync(destination, "arquivo existente");

        var output = await new WordReportService(_paths).GenerateToAsync(project, report, destination);

        Assert.Equal("arquivo existente", await File.ReadAllTextAsync(destination));
        Assert.EndsWith("final_v02.docx", output);
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp.docx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Section3UsesIndependentContinuousCounterAndEquipmentFiguresAreRenumbered()
    {
        var project = NewProject(); var report = project.CreateReport(); Configure(report);
        var (_, section) = AddSection(project, "Local", "Trecho", 0, 0); report.SelectSection(section);
        report.IncludeInspectionOpening = true; report.IncludeGpr = true;
        report.Equipments = EquipmentSelection.Endoscope | EquipmentSelection.Hunter;
        AddEvidence(AddOccurrence(section, OccurrenceLocationMode.Point, 5m, null, ["Fissura"]), 1, "Seção 4");
        AddDevelopmentImages(report, DevelopmentImageGroup.VideoInspection, 4);
        AddDevelopmentImages(report, DevelopmentImageGroup.InspectionOpening, 2);
        AddDevelopmentImages(report, DevelopmentImageGroup.Gpr, 5);

        var output = await new WordReportService(_paths).GenerateAsync(project, report);
        var text = ReadBody(output);

        Assert.Contains("Figuras 3.1 a 3.4", text);
        Assert.Contains("Figuras 3.5 a 3.6", text);
        Assert.Contains("Figuras 3.7 a 3.11", text);
        Assert.DoesNotContain("Figura 3.12", text);
        Assert.Contains("Figura 2.1 – Endoscópio", text);
        Assert.Contains("Figura 2.2 – Robô HUNTER", text);
        Assert.DoesNotContain("Figura 2.3", text);
        Assert.Contains("Figura 4.1", text);
    }

    [Fact]
    public async Task Stage6OperationalSmokeGeneratesCompleteWordWithTenAnomalies()
    {
        var project = NewProject();
        project.Name = "Projeto Smoke Etapa 6";
        var report = project.CreateReport("Relatório operacional");
        Configure(report, "GF-RL-STAGE6-001");
        report.Equipments = EquipmentSelection.Endoscope | EquipmentSelection.Hunter;
        report.IncludeInspectionOpening = true;
        report.IncludeGpr = true;
        var (_, section) = AddSection(project, "Local operacional", "Trecho operacional", 0, 0);
        report.SelectSection(section);

        var anomalyNames = new[]
        {
            "Fissura", "Trinca", "Infiltração", "Assoreamento", "Deformação",
            "Fissura", "Trinca", "Infiltração", "Assoreamento", "Deformação"
        };
        for (var index = 0; index < anomalyNames.Length; index++)
        {
            var occurrence = AddOccurrence(section, OccurrenceLocationMode.Point, index + 1, null,
                [anomalyNames[index]]);
            AddEvidence(occurrence, 1, $"Evidência {index + 1:00}");
        }

        AddDevelopmentImages(report, DevelopmentImageGroup.VideoInspection, 4);
        AddDevelopmentImages(report, DevelopmentImageGroup.InspectionOpening, 2);
        AddDevelopmentImages(report, DevelopmentImageGroup.Gpr, 3);
        var service = new WordReportService(_paths);

        var validation = service.Validate(project, report);
        Assert.True(validation.CanGenerate);
        Assert.Empty(validation.Warnings);
        Assert.Equal(10, validation.Occurrences);
        Assert.Equal(10, validation.Evidences);

        var output = await service.GenerateAsync(project, report);
        var text = ReadBody(output);

        Assert.True(File.Exists(output));
        Assert.Contains("GF-RL-STAGE6-001", text);
        Assert.Contains("TRECHO operacional", text);
        Assert.Contains("a 1 metro do início da filmagem", text);
        Assert.Contains("a 10 metros do início da filmagem", text);
        Assert.Contains("Figuras 3.7 a 3.9", text);
        Assert.Contains("Figura 4.10", text);
    }

    [Fact]
    public async Task WordUsesCentralOccurrenceFormatterIncludingRecordingInterruption()
    {
        var project = NewProject();
        var report = project.CreateReport("Relatório textual");
        Configure(report, "GF-RL-TEXT-001");
        var (_, section) = AddSection(project, "Local", "Trecho", 0, 0);
        report.SelectSection(section);
        var occurrence = AddOccurrence(section, OccurrenceLocationMode.Point, 25m, null,
            ["Trinca", "Assoreamento"]);
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.Anomalies[1].Position = AnomalyPosition.PipeWall;
        occurrence.InterruptedRecording = true;

        var output = await new WordReportService(_paths).GenerateAsync(project, report);
        var text = ReadBody(output);

        Assert.Contains("Trinca na geratriz superior e assoreamento na parede da tubulação a 25 metros do início da filmagem, interrompendo a gravação.", text);
    }

    [Fact]
    public async Task WordFigureCaptionOmitsMeterageAndFilmingReference()
    {
        var project = NewProject();
        var report = project.CreateReport("Relatório de legenda");
        Configure(report, "GF-RL-CAPTION-001");
        var (_, section) = AddSection(project, "Local", "Trecho", 0, 0);
        report.SelectSection(section);
        var occurrence = AddOccurrence(section, OccurrenceLocationMode.Point, 25m, null,
            ["Deslocamento das juntas"]);
        occurrence.Anomalies[0].Position = AnomalyPosition.UpperGenerator;
        occurrence.GeneratedDescription = OccurrenceRules.GenerateDescription(occurrence);
        AddEvidence(occurrence, 1, "Legenda antiga com metragem a 25 metros");

        var output = await new WordReportService(_paths).GenerateAsync(project, report);
        var text = ReadBody(output);

        Assert.Contains("Deslocamento das juntas na geratriz superior a 25 metros do início da filmagem", text);
        Assert.Contains("Figura 4.1 – Deslocamento das juntas na geratriz superior.", text);
        Assert.DoesNotContain("Figura 4.1 – Deslocamento das juntas na geratriz superior a 25 metros", text);
    }

    private Project NewProject() => new()
    {
        Id = Guid.NewGuid(), Name = "Projeto Etapa 5", CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow, ProjectPath = Path.Combine(_root, "project")
    };

    private static void Configure(Report report, string code = "GF-RL-STAGE5-001")
    {
        report.Client = "Cliente"; report.Cnpj = "00.000.000/0001-00"; report.Area = "Área técnica";
        report.Contract = "CT-05"; report.PurchaseOrder = "PC-05"; report.ReportCode = code;
        report.ReportDate = new DateTime(2026, 8, 11); report.LocationDescription = "Localização técnica";
        report.TotalInvestigatedMeters = 150m; report.Diameters = StandardDiameterSelection.Mm600;
    }

    private static IReadOnlyList<string> ReadParagraphs(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Select(x => string.Concat(x.Descendants<Text>().Select(t => t.Text))).ToList();
    }

    private static int IndexOf(IReadOnlyList<string> values, string expected)
    {
        var index = values.ToList().FindIndex(x => x == expected);
        Assert.True(index >= 0, $"Parágrafo não encontrado: {expected}");
        return index;
    }

    private static (Location Location, InspectionSection Section) AddSection(Project project, string locationName,
        string sectionName, int locationOrder, int sectionOrder)
    {
        var location = new Location { ProjectId = project.Id, Name = locationName, Order = locationOrder };
        var section = new InspectionSection { LocationId = location.Id, Name = sectionName, Order = sectionOrder };
        location.Sections.Add(section); project.Locations.Add(location); return (location, section);
    }

    private static Occurrence AddOccurrence(InspectionSection section, OccurrenceLocationMode mode,
        decimal? start, decimal? end, IReadOnlyList<string> anomalies)
    {
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id, LocationMode = mode, Meterage = start, EndMeterage = end,
            Order = section.Occurrences.Count, CreatedAt = DateTime.UtcNow
        };
        occurrence.Anomalies.AddRange(anomalies.Select((name, index) =>
        {
            var definition = AnomalyCatalog.Defaults.Single(x => x.Name == name);
            return new OccurrenceAnomaly
            {
                OccurrenceId = occurrence.Id, AnomalyTypeId = definition.Id,
                AnomalyType = definition, Order = index
            };
        }));
        OccurrenceRules.Validate(occurrence);
        occurrence.GeneratedDescription = OccurrenceRules.GenerateDescription(occurrence);
        section.Occurrences.Add(occurrence);
        return occurrence;
    }

    private void AddEvidence(Occurrence occurrence, int count, string caption)
    {
        for (var index = 0; index < count; index++) occurrence.Evidence.Add(new Evidence
        {
            OccurrenceId = occurrence.Id, SourceType = EvidenceSourceType.ExternalImage,
            FilePath = _image, Caption = $"{caption} {index + 1}", Order = index
        });
    }

    private void AddDevelopmentImages(Report report, DevelopmentImageGroup group, int count)
    {
        for (var index = 0; index < count; index++) report.DevelopmentImages.Add(new DevelopmentImage
        {
            ReportId = report.Id, Group = group, FilePath = _image, Order = index
        });
    }

    private static string ReadBody(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return string.Concat(document.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(x => x.Text));
    }
}
