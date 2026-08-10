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
    private string _location = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(_root);
        _store = new ProjectStore(_paths);
        await _store.InitializeAsync();
        _template = Path.Combine(AppContext.BaseDirectory, "Templates", "modelo folha.docx");
        _logo = Path.Combine(_root, "logo.png");
        _location = Path.Combine(_root, "localizacao.png");
        await File.WriteAllBytesAsync(_logo, Convert.FromBase64String(TinyPng));
        await File.WriteAllBytesAsync(_location, Convert.FromBase64String(TinyPng));
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
        Assert.Empty(recovered.Reports.Single(x => x.Id == second.Id).CustomSections);
    }

    [Fact]
    public async Task GeneratesValidCustomizedCopyWithoutChangingOfficialTemplate()
    {
        var (project, report) = await CreateConfiguredProjectAsync();
        var originalHash = Hash(_template);
        var output = await new WordReportService(_paths).GenerateAsync(project, report, includeSection4TechnicalProof: true);

        Assert.True(File.Exists(output));
        Assert.Equal(originalHash, Hash(_template));
        using var doc = WordprocessingDocument.Open(output, false);
        var main = doc.MainDocumentPart!;
        var bodyText = string.Concat(main.Document.Body!.Descendants<Text>().Select(x => x.Text));
        Assert.Contains("CLIENTE: Cliente de teste", bodyText);
        Assert.Contains("ÁREA: Área de teste", bodyText);
        Assert.Contains("CONTRATO: CT-2026", bodyText);
        Assert.Contains("GF-RL-TESTE-001", bodyText);
        Assert.Contains("400 mm, 600 mm e 900 mm", bodyText);
        Assert.Contains("Etapa especial", bodyText);
        Assert.Contains("HISTÓRICO DE ALTERAÇÕES", bodyText);
        Assert.Contains("TRECHOS, REGISTROS E EVIDÊNCIAS", bodyText);
        Assert.DoesNotContain("RUA TESTE", bodyText);
        Assert.Single(main.Document.Body.Descendants<SectionProperties>());
        var contentCell = main.Document.Body.Elements<Table>().First()
            .Elements<TableRow>().Last().Elements<TableCell>().First();
        Assert.Contains("1. IDENTIFICAÇÃO", string.Concat(contentCell.Descendants<Text>().Select(x => x.Text)));
        Assert.True(main.DocumentSettingsPart!.Settings!.GetFirstChild<UpdateFieldsOnOpen>()!.Val!.Value);
        Assert.True(main.ImageParts.Count() >= 2);
    }

    [Fact]
    public async Task UsesBundledStandardTemplateWhenNoTemplateWasSelected()
    {
        var project = await _store.CreateAsync("Template padrão");
        var report = project.CreateReport();
        report.Client = "Cliente padrão";
        report.Diameters = StandardDiameterSelection.Mm400;

        var service = new WordReportService(_paths);
        var output = await service.GenerateAsync(project, report);

        Assert.Equal(service.DefaultTemplatePath, report.TemplatePath);
        Assert.Contains("CLIENTE: Cliente padrão", ReadBody(output));
        Assert.Contains("HISTÓRICO DE ALTERAÇÕES", ReadBody(output));
    }

    [Fact]
    public async Task MigratesLegacyTemplateSelectionToBundledStandard()
    {
        var project = await _store.CreateAsync("Migração de template");
        var report = project.CreateReport();
        report.TemplatePath = Path.Combine(_root, "MODELO DE RELATORIO.docx");
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
        second.Equipments = EquipmentSelection.Endoscope;
        second.Diameters = StandardDiameterSelection.Mm400;
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
        report.TemplatePath = _template; report.LocationImagePath = _location;
        report.Equipments = EquipmentSelection.Endoscope | EquipmentSelection.Bug;
        report.Diameters = StandardDiameterSelection.Mm400 | StandardDiameterSelection.Mm600;
        report.CustomDiameters = "900"; report.IncludeInspectionOpening = true;
        var custom = new CustomSection { ProjectId = project.Id, ReportId = report.Id, Title = "Etapa especial", Text = "Texto personalizado persistido e inserido no relatório.", Order = 0 };
        custom.Images.Add(new CustomSectionImage { CustomSectionId = custom.Id, FilePath = _logo, Order = 0 });
        report.CustomSections.Add(custom);
        await _store.SaveAsync(project);
        return (project, report);
    }

    private static string ReadBody(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return string.Concat(doc.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(x => x.Text));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
