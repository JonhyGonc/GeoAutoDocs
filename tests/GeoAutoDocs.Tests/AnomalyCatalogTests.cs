using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class AnomalyCatalogTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsAnomalyCatalogTests", Guid.NewGuid().ToString("N"));
    private AppDataPaths _paths = null!;
    private AnomalyCatalogService _service = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(_root);
        await new ProjectStore(_paths).InitializeAsync();
        _service = new AnomalyCatalogService(_paths);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CustomDefinitionPersistsAndCanBeDeactivated()
    {
        var saved = await _service.SaveAsync(new AnomalyDefinition
        {
            Name = "Incrustação ácida", DescriptionTemplate = "{nome}: {dimensao} - {textoComplementar}",
            SupportsDimension = true, SupportsComplementaryText = true
        });
        await _service.SetActiveAsync(saved.Id, false);
        var reopened = Assert.Single(await _service.ListAsync(), x => x.Id == saved.Id);
        Assert.Equal("Incrustação ácida", reopened.Name);
        Assert.False(reopened.IsActive);
        Assert.False(reopened.IsBuiltIn);
    }

    [Fact]
    public async Task DuplicateNameIsRejectedIgnoringCase()
    {
        await _service.SaveAsync(new AnomalyDefinition { Name = "Anomalia exclusiva" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SaveAsync(new AnomalyDefinition { Name = "anomalia EXCLUSIVA" }));
    }

    [Fact]
    public void TemplateRejectsUnknownVariableAndRendersAllowedValues()
    {
        var definition = new AnomalyDefinition { Name = "Teste", DescriptionTemplate = "{nome}: {dimensao}." };
        var rendered = AnomalyTemplate.Render(definition.DescriptionTemplate, definition,
            new OccurrenceAnomaly { AnomalyType = definition, AnomalyName = definition.Name, Dimension = "30 mm" });
        Assert.Equal("Teste: 30 mm.", rendered);
        Assert.Throws<InvalidOperationException>(() => AnomalyTemplate.Validate("{codigoMalicioso}"));
    }

    [Fact]
    public async Task SedimentAccumulationIsSeededOnceAndKeepsMaterialDeposited()
    {
        await new ProjectStore(_paths).InitializeAsync();
        var definitions = await _service.ListAsync();
        var sediment = Assert.Single(definitions, x => x.Id == AnomalyCatalog.SedimentAccumulationId);
        Assert.Equal("Acúmulo de sedimentos", sediment.Name);
        Assert.True(sediment.IsActive);
        Assert.True(sediment.SupportsPosition);
        Assert.Single(definitions, x => x.Name == "Material depositado");
    }

    [Fact]
    public void SedimentAccumulationDescriptionIncludesSelectedPosition()
    {
        var definition = AnomalyCatalog.Defaults.Single(x => x.Id == AnomalyCatalog.SedimentAccumulationId);
        var occurrence = new Occurrence
        {
            InspectionSectionId = Guid.NewGuid(), LocationMode = OccurrenceLocationMode.Point, Meterage = 10,
            Anomalies =
            [
                new OccurrenceAnomaly
                {
                    AnomalyTypeId = definition.Id, AnomalyType = definition,
                    AnomalyName = definition.Name, Position = AnomalyPosition.LowerGenerator
                }
            ]
        };

        Assert.Contains("Acúmulo de sedimentos na geratriz inferior", OccurrenceRules.GenerateDescription(occurrence));
    }
}
