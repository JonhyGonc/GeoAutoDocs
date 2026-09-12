using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class InspectionExchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsExchangeTests", Guid.NewGuid().ToString("N"));
    private readonly AppDataPaths _paths;

    public InspectionExchangeTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppDataPaths(_root);
        _paths.EnsureDirectories();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void InspectionExchangeSerializesStructuredDataAndRoundTrips()
    {
        var (project, report, occurrence) = CreateInspection();
        var service = new InspectionExchangeService();
        var serializer = service.Serializer;

        var original = service.Build(project, report, new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc));
        var json = serializer.Serialize(original);
        var reopened = serializer.Deserialize(json);

        Assert.Equal(InspectionExchangeSchema.Name, reopened.Schema);
        Assert.Equal(1, reopened.SchemaVersion);
        Assert.Equal(project.Id, reopened.Project.ProjectId);
        Assert.Equal(report.Id, reopened.Report.ReportId);
        var exported = Assert.Single(Assert.Single(reopened.Sections).Occurrences);
        Assert.Equal("MultiPoint", exported.LocationMode);
        Assert.Equal([12m, 13m], exported.MeteragePoints);
        Assert.Equal(occurrence.EffectiveDescription, exported.EffectiveDescription);
        Assert.Equal("UpperAndLowerGenerators", Assert.Single(exported.Anomalies).Position);
        Assert.Single(exported.Evidence);
        Assert.DoesNotContain(Path.GetFullPath(_root), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iVBOR", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportGroupsSectionsByLocationAndPreservesCreationOrder()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Ordem de criação" };
        var report = project.CreateReport("Relatório ordenado");

        var firstLocation = new Location
        {
            ProjectId = project.Id,
            Name = "PVAP09",
            Order = 0
        };
        var secondLocation = new Location
        {
            ProjectId = project.Id,
            Name = "PVAP01",
            Order = 1
        };
        var firstSection = new InspectionSection
        {
            LocationId = firstLocation.Id,
            Name = "Trecho criado primeiro",
            Order = 0
        };
        var secondSection = new InspectionSection
        {
            LocationId = firstLocation.Id,
            Name = "Trecho criado depois",
            Order = 1
        };
        var thirdSection = new InspectionSection
        {
            LocationId = secondLocation.Id,
            Name = "Trecho do segundo local",
            Order = 0
        };

        firstLocation.Sections.Add(secondSection);
        firstLocation.Sections.Add(firstSection);
        secondLocation.Sections.Add(thirdSection);
        project.Locations.Add(secondLocation);
        project.Locations.Add(firstLocation);

        report.SelectSection(thirdSection);
        report.SelectSection(secondSection);
        report.SelectSection(firstSection);

        var document = new InspectionExchangeService().Build(project, report);

        Assert.Equal(
            new[] { firstLocation.Id, secondLocation.Id },
            document.Locations.Select(x => x.LocationId));
        Assert.Equal(
            new[] { firstSection.Id, secondSection.Id, thirdSection.Id },
            document.Sections.Select(x => x.SectionId));
        Assert.Equal(
            new[] { firstLocation.Id, firstLocation.Id, secondLocation.Id },
            document.Sections.Select(x => x.LocationId));
    }

    [Fact]
    public async Task GeneratedDocxContainsReadableManifestAndMatchingSidecar()
    {
        var (project, report, _) = CreateInspection();
        var word = new WordReportService(_paths);
        var exchange = new InspectionExchangeService();
        var serializer = exchange.Serializer;

        var output = await word.GenerateAsync(project, report);
        var sidecarPath = FileInspectionTransport.GetSidecarPath(output);

        Assert.True(File.Exists(output));
        Assert.True(File.Exists(sidecarPath));
        using (var docx = WordprocessingDocument.Open(output, false))
        {
            var body = string.Concat(docx.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(x => x.Text));
            Assert.Contains("Infiltração na geratriz inferior a 12 e 13 metros do início da filmagem", body);
        }
        var sidecar = serializer.Deserialize(await File.ReadAllTextAsync(sidecarPath));
        Assert.True(exchange.TryReadInspectionManifest(output, out var manifest, out var error), error);
        Assert.NotNull(manifest);
        Assert.Equal(serializer.Serialize(sidecar), serializer.Serialize(manifest!));
    }

    [Fact]
    public async Task SharedProjectWithoutMeteragePointsFieldStillLoads()
    {
        var directory = Path.Combine(_root, "shared-old");
        var (project, _, _) = CreateInspection(OccurrenceLocationMode.Point);
        var service = new SharedProjectService(_paths);
        var editor = await service.OpenAsync(project, directory);
        await editor.CloseAsync();
        var path = Path.Combine(directory, "project.geoadocs.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path));
        RemoveProperties(root, "MeteragePoints");
        await File.WriteAllTextAsync(path, root!.ToJsonString());

        var reader = await service.OpenAsync(new Project { Id = project.Id, Name = project.Name }, directory, readOnly: true);
        var occurrence = Assert.Single(Assert.Single(Assert.Single(reader.Project.Locations).Sections).Occurrences);
        Assert.Equal(OccurrenceLocationMode.Point, occurrence.LocationMode);
        Assert.Equal(12m, occurrence.Meterage);
        Assert.Empty(occurrence.MeteragePoints);
        await reader.CloseAsync();
    }

    [Fact]
    public async Task ExistingSqliteDatabaseReceivesMultiPointTableIncrementally()
    {
        var paths = new AppDataPaths(Path.Combine(_root, "sqlite-upgrade"));
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE \"OccurrenceMeteragePoint\"";
            await drop.ExecuteNonQueryAsync();
        }

        await store.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await verification.OpenAsync();
        await using var command = verification.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OccurrenceMeteragePoint'";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    private (Project Project, Report Report, Occurrence Occurrence) CreateInspection(
        OccurrenceLocationMode mode = OccurrenceLocationMode.MultiPoint)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Projeto de intercâmbio", CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow, ProjectPath = Path.Combine(_root, "project")
        };
        var report = project.CreateReport("Relatório de intercâmbio");
        report.Client = "Cliente";
        report.Area = "Área";
        report.Contract = "CT-01";
        report.PurchaseOrder = "PC-01";
        report.ReportCode = "GF-RL-EXCHANGE-001";
        report.ReportDate = new DateTime(2026, 8, 21);
        report.TotalInvestigatedMeters = 100m;
        report.Diameters = StandardDiameterSelection.Mm600;
        var location = new Location { ProjectId = project.Id, Type = LocationType.Street, Name = "Rua A", Order = 2 };
        var section = new InspectionSection
        {
            LocationId = location.Id, Name = "Trecho A", Description = "Trecho principal", Diameter = 600m,
            StartPoint = "PV-01", EndPoint = "PV-02", Reference = "Sentido norte", InvestigatedMeters = 100m, Order = 3
        };
        var videoPath = Path.Combine(_root, "video-inspecao.mp4");
        section.VideoReferences.Add(new VideoReference
        {
            InspectionSectionId = section.Id, FilePath = videoPath, DisplayName = "Vídeo 01", Order = 0
        });
        var anomaly = AnomalyCatalog.Defaults.Single(x => x.Name == "Infiltração");
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id, LocationMode = mode, Meterage = 12m,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Order = 4,
            Notes = "Observação técnica", GeneratedDescription = "Descrição estruturada"
        };
        if (mode == OccurrenceLocationMode.MultiPoint)
            occurrence.MeteragePoints.AddRange([
                new OccurrenceMeteragePoint { OccurrenceId = occurrence.Id, Meterage = 12m, Order = 0 },
                new OccurrenceMeteragePoint { OccurrenceId = occurrence.Id, Meterage = 13m, Order = 1 }
            ]);
        occurrence.Anomalies.Add(new OccurrenceAnomaly
        {
            OccurrenceId = occurrence.Id, AnomalyTypeId = anomaly.Id, AnomalyType = anomaly,
            AnomalyName = anomaly.Name, Position = AnomalyPosition.UpperAndLowerGenerators, Order = 0
        });
        var evidencePath = Path.Combine(_root, "evidence-do-not-export.png");
        File.WriteAllBytes(evidencePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nEkAAAAASUVORK5CYII="));
        occurrence.Evidence.Add(new Evidence
        {
            OccurrenceId = occurrence.Id, SourceType = EvidenceSourceType.VideoCapture,
            VideoReferenceId = section.VideoReferences[0].Id, VideoTimestamp = TimeSpan.FromSeconds(15),
            FilePath = evidencePath, Caption = "Frame principal", IsPrimary = true
        });
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        report.SelectSection(section);
        return (project, report, occurrence);
    }

    private static void RemoveProperties(JsonNode? node, string name)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(name);
            foreach (var child in obj.ToList()) RemoveProperties(child.Value, name);
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemoveProperties(child, name);
    }
}
