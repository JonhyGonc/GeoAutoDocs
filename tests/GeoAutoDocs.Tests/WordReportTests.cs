using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class WordReportTests : IAsyncLifetime
{
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nEkAAAAASUVORK5CYII=";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsWordTests", Guid.NewGuid().ToString("N"));
    private AppDataPaths _paths = null!;
    private ProjectStore _store = null!;
    private string _template = null!;
    private string _logo = null!;
    private string _locationImage = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(_root);
        _store = new ProjectStore(_paths);
        await _store.InitializeAsync();
        _template = Path.Combine(AppContext.BaseDirectory, "Templates", "MODELO DE RELATORIO.docx");
        _logo = Path.Combine(_root, "logo.png");
        _locationImage = Path.Combine(_root, "localizacao.png");
        await File.WriteAllBytesAsync(_logo, Convert.FromBase64String(TinyPng));
        await File.WriteAllBytesAsync(_locationImage, Convert.FromBase64String(TinyPng));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PersistsMultipleReportConfigurationsAndReportOwnedCustomContent()
    {
        var (project, report) = await CreateConfiguredProjectAsync();
        report.DevelopmentImages.Add(new DevelopmentImage
        {
            ReportId = report.Id, Group = DevelopmentImageGroup.VideoInspection,
            FilePath = _locationImage, Order = 0
        });
        var second = project.CreateReport(); second.Client = "Segundo cliente";
        await _store.SaveAsync(project);
        var recovered = await new ProjectStore(new AppDataPaths(_root)).GetAsync(project.Id);

        Assert.Equal(2, recovered!.Reports.Count);
        var first = recovered.Reports.Single(x => x.Id == report.Id);
        Assert.Equal(_template, first.TemplatePath);
        Assert.Equal(EquipmentSelection.Endoscope | EquipmentSelection.Bug, first.Equipments);
        var custom = Assert.Single(first.CustomSections);
        Assert.Equal("Etapa especial", custom.Title);
        Assert.Equal(_logo, Assert.Single(custom.Images).FilePath);
        Assert.Equal(_locationImage, Assert.Single(first.DevelopmentImages).FilePath);
        Assert.Empty(recovered.Reports.Single(x => x.Id == second.Id).CustomSections);
    }

    [Fact]
    public async Task GeneratesCustomizedCopyWhilePreservingTheCompleteOfficialReport()
    {
        var (project, report) = await CreateConfiguredProjectAsync();
        AddDynamicResults(project, report);
        var originalHash = Hash(_template);

        var output = await new WordReportService(_paths).GenerateAsync(project, report);

        Assert.True(File.Exists(output));
        Assert.Equal(originalHash, Hash(_template));
        using var doc = WordprocessingDocument.Open(output, false);
        var main = doc.MainDocumentPart!;
        var bodyText = string.Concat(main.Document.Body!.Descendants<Text>().Select(x => x.Text));

        Assert.Contains("CLIENTE: Cliente de teste", bodyText);
        Assert.Contains("ÁREA: Área de teste", bodyText);
        Assert.Contains("CONTRATO: CT-2026", bodyText);
        Assert.Contains("GF-RL-TESTE-001", bodyText);
        Assert.Contains("Localização de teste", bodyText);
        Assert.Contains("aproximadamente 123,45 metros", bodyText);
        Assert.Contains("400 mm, 600 mm e 900 mm", bodyText);

        Assert.Contains("ÍNDICE", bodyText);
        Assert.Contains("INTRODUÇÃO E DESCRIÇÃO DOS SERVIÇOS", bodyText);
        Assert.Contains("EQUIPAMENTOS UTILIZADOS", bodyText);
        Assert.Contains("DESENVOLVIMENTO DO TRABALHO", bodyText);
        Assert.Contains("Resultados e Conclusões", bodyText);
        Assert.Contains("O presente trabalho constitui uma etapa importante", bodyText);
        Assert.Contains("Desenvolvido para atender nossos clientes", bodyText);
        Assert.Contains("Através dos serviços executados foi possível identificar", bodyText);
        Assert.Contains("As anomalias estão descritas", bodyText);
        Assert.Contains("Ocorrência dinâmica de prova (grave)", bodyText);
        var severeRun = Assert.Single(main.Document.Body!.Descendants<Run>(),
            x => x.InnerText.Trim().Equals("(grave)", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("FF0000", severeRun.RunProperties?.Color?.Val?.Value);
        Assert.Contains("Felipe França", bodyText);
        Assert.Contains("CREA: 5069912165", bodyText);

        Assert.Contains("O endoscópio industrial", bodyText);
        Assert.Contains("Para diâmetros de até 400 mm", bodyText);
        Assert.DoesNotContain("Para diâmetros maiores, de 400 mm", bodyText);
        Assert.DoesNotContain("Para diâmetros maiores de 600 mm", bodyText);
        Assert.Contains("Além disso, foram realizadas intervenções", bodyText);
        Assert.DoesNotContain("Em complemento à investigação", bodyText);

        Assert.Contains("TRECHO de prova", bodyText);
        Assert.Contains("Ocorrência dinâmica de prova", bodyText);
        Assert.Contains("Figura 4.1", bodyText);
        Assert.Contains("Etapa especial", bodyText);
        Assert.DoesNotContain("RUA RENATO CAETANO DA SILVA FILHO", bodyText);

        Assert.DoesNotContain("1. IDENTIFICAÇÃO", bodyText);
        Assert.DoesNotContain("OBJETO E ESCOPO", bodyText);
        Assert.DoesNotContain("EQUIPAMENTOS E PARÂMETROS", bodyText);
        Assert.DoesNotContain("TRECHOS, REGISTROS E EVIDÊNCIAS", bodyText);
        Assert.DoesNotContain("CONTEÚDO COMPLEMENTAR", bodyText);
        Assert.DoesNotContain("Este relatório reúne as informações registradas", bodyText);
        Assert.Equal(3, main.Document.Body.Descendants<SectionProperties>().Count());
        Assert.True(main.DocumentSettingsPart!.Settings!.GetFirstChild<UpdateFieldsOnOpen>()!.Val!.Value);
        Assert.True(main.ImageParts.Count() >= 20);
    }

    [Fact]
    public async Task UsesBundledCompleteTemplateWhenNoTemplateWasSelected()
    {
        var project = await _store.CreateAsync("Template padrão");
        var report = project.CreateReport();
        report.Client = "Cliente padrão";
        report.Area = "Área de teste";
        report.ReportCode = "GF-RL-PADRAO-001";
        report.Contract = "CT-PADRAO";
        report.PurchaseOrder = "PC-PADRAO";
        report.LocationDescription = "Localização padrão";
        report.ReportDate = new DateTime(2026, 8, 11);
        report.TotalInvestigatedMeters = 10;
        report.Diameters = StandardDiameterSelection.Mm400;
        var location = new Location { ProjectId = project.Id, Name = "Local padrão" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho padrão" };
        location.Sections.Add(section); project.Locations.Add(location); report.SelectSection(section);

        var service = new WordReportService(_paths);
        var output = await service.GenerateAsync(project, report);

        Assert.Equal(service.DefaultTemplatePath, report.TemplatePath);
        var bodyText = ReadBody(output);
        Assert.Contains("CLIENTE: Cliente padrão", bodyText);
        Assert.Contains("ÍNDICE", bodyText);
        Assert.Contains("HISTÓRICO DE ALTERAÇÕES", bodyText);
        Assert.Contains("Felipe França", bodyText);
    }

    [Fact]
    public async Task MigratesLegacyTemplateSelectionToBundledStandard()
    {
        var project = await _store.CreateAsync("Migração de template");
        var report = project.CreateReport();
        report.TemplatePath = Path.Combine(_root, "modelo folha.docx");
        var service = new WordReportService(_paths);

        Assert.True(service.ApplyStandardTemplate(project));
        Assert.Equal(service.DefaultTemplatePath, report.TemplatePath);
        Assert.False(service.ApplyStandardTemplate(project));
    }

    [Fact]
    public async Task TwoReportsGenerateIndependentDocxFilesAndHeaders()
    {
        var (project, first) = await CreateConfiguredProjectAsync();
        project.DefaultClient = "Cliente padrão 002";
        project.DefaultContract = "CT-002";
        var second = project.CreateReport();
        second.TemplatePath = _template; second.ReportCode = "GF-RL-TESTE-002";
        second.ReportDate = new DateTime(2026, 8, 11);
        second.LocationDescription = "Localização 002"; second.TotalInvestigatedMeters = 50;
        second.Equipments = EquipmentSelection.Endoscope;
        second.Diameters = StandardDiameterSelection.Mm400;
        var location = new Location { ProjectId = project.Id, Name = "Local dos relatórios" };
        var section001 = new InspectionSection { LocationId = location.Id, Name = "Trecho 001", Order = 0 };
        var section002 = new InspectionSection { LocationId = location.Id, Name = "Trecho 002", Order = 1 };
        location.Sections.AddRange([section001, section002]); project.Locations.Add(location);
        first.SelectSection(section001); second.SelectSection(section002);
        var service = new WordReportService(_paths);

        var output001 = await service.GenerateAsync(project, first);
        var output002 = await service.GenerateAsync(project, second);

        Assert.NotEqual(output001, output002);
        Assert.Equal("Cliente de teste", first.Client);
        Assert.Contains("CLIENTE: Cliente de teste", ReadBody(output001));
        Assert.Contains("CLIENTE: Cliente padrão 002", ReadBody(output002));
        Assert.Contains("GF-RL-TESTE-001", ReadBody(output001));
        Assert.Contains("GF-RL-TESTE-002", ReadBody(output002));
    }

    private async Task<(Project Project, Report Report)> CreateConfiguredProjectAsync()
    {
        var project = await _store.CreateAsync("Prova Etapa 2.5");
        project.DefaultClient = "Cliente de teste"; project.DefaultArea = "Área de teste";
        project.DefaultContract = "CT-2026"; project.DefaultPurchaseOrder = "PC-42"; project.DefaultLogoPath = _logo;
        var report = project.CreateReport();
        report.ReportCode = "GF-RL-TESTE-001"; report.ReportDate = new DateTime(2026, 8, 10);
        report.LocationDescription = "Localização de teste"; report.TotalInvestigatedMeters = 123.45m;
        report.TemplatePath = _template; report.LocationImagePath = _locationImage;
        report.Equipments = EquipmentSelection.Endoscope | EquipmentSelection.Bug;
        report.Diameters = StandardDiameterSelection.Mm400 | StandardDiameterSelection.Mm600;
        report.CustomDiameters = "900"; report.IncludeInspectionOpening = true;
        var custom = new CustomSection { ProjectId = project.Id, ReportId = report.Id, Title = "Etapa especial", Text = "Texto personalizado persistido e inserido no relatório.", Order = 0 };
        custom.Images.Add(new CustomSectionImage { CustomSectionId = custom.Id, FilePath = _logo, Order = 0 });
        report.CustomSections.Add(custom);
        await _store.SaveAsync(project);
        return (project, report);
    }

    private void AddDynamicResults(Project project, Report report)
    {
        var location = new Location { ProjectId = project.Id, Name = "Local de prova", Order = 0 };
        var section = new InspectionSection
        {
            LocationId = location.Id,
            Name = "Trecho de prova",
            Reference = "rede principal",
            Diameter = 600,
            Order = 0
        };
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            CustomGeneratedText = "Ocorrência dinâmica de prova",
            IsSevere = true,
            Order = 0
        };
        occurrence.Evidence.Add(new Evidence
        {
            OccurrenceId = occurrence.Id,
            FilePath = _locationImage,
            Caption = "Evidência dinâmica de prova",
            Order = 0
        });
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        report.SelectSection(section);
    }

    private static string ReadBody(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return string.Concat(doc.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(x => x.Text));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
