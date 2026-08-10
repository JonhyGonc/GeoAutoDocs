using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;

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
        _template = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MODELO DE RELATORIO.docx");
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
    public async Task PersistsCompleteReportConfigurationAndCustomContent()
    {
        var project = await CreateConfiguredProjectAsync();
        var reopened = new ProjectStore(new AppDataPaths(_root));
        await reopened.InitializeAsync();
        var recovered = await reopened.GetAsync(project.Id);

        Assert.NotNull(recovered);
        Assert.Equal(_template, recovered.ReportConfiguration.TemplatePath);
        Assert.Equal(EquipmentSelection.Endoscope | EquipmentSelection.Bug, recovered.ReportConfiguration.Equipments);
        Assert.Equal(StandardDiameterSelection.Mm400 | StandardDiameterSelection.Mm600, recovered.ReportConfiguration.Diameters);
        Assert.True(recovered.ReportConfiguration.IncludeInspectionOpening);
        Assert.False(recovered.ReportConfiguration.IncludeGpr);
        var custom = Assert.Single(recovered.CustomSections);
        Assert.Equal("Etapa especial", custom.Title);
        Assert.Equal(_logo, Assert.Single(custom.Images).FilePath);
    }

    [Fact]
    public async Task GeneratesValidCustomizedCopyWithoutChangingTemplate()
    {
        var project = await CreateConfiguredProjectAsync();
        var originalHash = Hash(_template);
        var service = new WordReportService(_paths);

        var output = await service.GenerateAsync(project, includeSection4TechnicalProof: true);

        Assert.True(File.Exists(output));
        Assert.NotEqual(Path.GetFullPath(_template), Path.GetFullPath(output));
        Assert.Equal(originalHash, Hash(_template));
        using var doc = WordprocessingDocument.Open(output, false);
        var main = doc.MainDocumentPart!;
        var bodyText = string.Concat(main.Document.Body!.Descendants<Text>().Select(x => x.Text));
        Assert.Contains("CLIENTE: Cliente de teste", bodyText);
        Assert.Contains("ÁREA: Área de teste", bodyText);
        Assert.Contains("CONTRATO: CT-2026", bodyText);
        Assert.Contains("PEDIDO DE COMPRA: PC-42", bodyText);
        Assert.Contains("GF-RL-TESTE-001", bodyText);
        Assert.Contains("400 mm, 600 mm e 900 mm", bodyText);
        Assert.Contains("Endoscópio", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Robô BUG", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("robô HUNTER", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("robô SPIDER", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intervenções construtivas", bodyText);
        Assert.DoesNotContain("sondagens não intrusivas", bodyText);
        Assert.Contains("Etapa especial", bodyText);
        Assert.Contains("RUA TESTE", bodyText);
        Assert.Contains("Figura 4.3 – Evidência", bodyText);
        Assert.DoesNotContain("Figura 4.4 –", bodyText);
        Assert.True(main.DocumentSettingsPart!.Settings!.GetFirstChild<UpdateFieldsOnOpen>()!.Val!.Value);
        Assert.NotEmpty(main.Document.Body.Descendants<A.Blip>());
    }

    [Fact]
    public async Task GeneratesNewVersionWhenReportAlreadyExists()
    {
        var project = await CreateConfiguredProjectAsync();
        var service = new WordReportService(_paths);

        var first = await service.GenerateAsync(project);
        var firstHash = Hash(first);
        var second = await service.GenerateAsync(project);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(firstHash, Hash(first));
    }

    private async Task<Project> CreateConfiguredProjectAsync()
    {
        var project = await _store.CreateAsync("Prova Etapa 2");
        var report = project.ReportConfiguration;
        report.Client = "Cliente de teste";
        report.Area = "Área de teste";
        report.Contract = "CT-2026";
        report.PurchaseOrder = "PC-42";
        report.ReportCode = "GF-RL-TESTE-001";
        report.ReportDate = new DateTime(2026, 8, 10);
        report.LocationDescription = "Localização de teste";
        report.TotalInvestigatedMeters = 123.45m;
        report.TemplatePath = _template;
        report.LogoPath = _logo;
        report.LocationImagePath = _location;
        report.Equipments = EquipmentSelection.Endoscope | EquipmentSelection.Bug;
        report.Diameters = StandardDiameterSelection.Mm400 | StandardDiameterSelection.Mm600;
        report.CustomDiameters = "900";
        report.IncludeInspectionOpening = true;
        report.IncludeGpr = false;
        var custom = new CustomSection
        {
            ProjectId = project.Id,
            Title = "Etapa especial",
            Text = "Texto personalizado persistido e inserido no relatório.",
            Order = 0
        };
        custom.Images.Add(new CustomSectionImage { CustomSectionId = custom.Id, FilePath = _logo, Order = 0 });
        project.CustomSections.Add(custom);
        await _store.SaveAsync(project);
        return project;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
