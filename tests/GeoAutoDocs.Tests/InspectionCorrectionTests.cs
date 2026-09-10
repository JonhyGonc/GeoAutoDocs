using System.Text;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class InspectionCorrectionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GeoAutoDocsCorrectionTests", Guid.NewGuid().ToString("N"));
    private ProjectStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new ProjectStore(new AppDataPaths(_root));
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AppliesOnlyExplicitFieldsAndPersistsIdentityAndRelations()
    {
        var project = await _store.CreateAsync("Correções");
        var location = new Location { ProjectId = project.Id, Name = "PV 07" };
        var section = new InspectionSection
        {
            LocationId = location.Id,
            Name = "Trecho 01",
            StartPoint = "PV 07",
            EndPoint = "PV 08"
        };
        var video = new VideoReference
        {
            InspectionSectionId = section.Id,
            DisplayName = "Vídeo 01",
            FilePath = @"C:\videos\pv07.avi"
        };
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            VideoReferenceId = video.Id,
            LocationMode = OccurrenceLocationMode.Point,
            Meterage = 12.50m,
            GeneratedDescription = "DESLOCAMENTO DE JUNTA",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var evidence = new Evidence
        {
            OccurrenceId = occurrence.Id,
            VideoReferenceId = video.Id,
            FilePath = @"C:\evidencias\pv07.png",
            SourceType = EvidenceSourceType.ExternalImage
        };
        occurrence.Evidence.Add(evidence);
        section.VideoReferences.Add(video);
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        var report = project.CreateReport();
        report.SelectSection(section);
        await _store.SaveAsync(project);

        var service = new InspectionCorrectionService();
        var plan = service.Prepare(project, new InspectionCorrectionPackage
        {
            ReportId = report.Id,
            Corrections =
            [
                new InspectionCorrection
                {
                    SectionId = section.Id,
                    OccurrenceId = occurrence.Id,
                    Meterage = 13.75m
                }
            ]
        });

        var snapshots = service.Apply(plan);
        await _store.SaveAsync(project);

        Assert.Single(snapshots);
        var recovered = await _store.GetAsync(project.Id);
        var savedSection = Assert.Single(Assert.Single(recovered!.Locations).Sections);
        var savedOccurrence = Assert.Single(savedSection.Occurrences);
        Assert.Equal(occurrence.Id, savedOccurrence.Id);
        Assert.Equal(13.75m, savedOccurrence.Meterage);
        Assert.Equal("DESLOCAMENTO DE JUNTA", savedOccurrence.EffectiveDescription);
        Assert.Equal(video.Id, savedOccurrence.VideoReferenceId);
        Assert.Equal(evidence.Id, Assert.Single(savedOccurrence.Evidence).Id);
        Assert.Equal(video.Id, Assert.Single(savedSection.VideoReferences).Id);
        Assert.Equal(section.Id, Assert.Single(Assert.Single(recovered.Reports).SectionLinks).SectionId);
    }

    [Fact]
    public void MissingOccurrenceIsReportedAndNeverCreated()
    {
        var (project, report, section, occurrence) = NewProject();
        var missingId = Guid.NewGuid();
        var service = new InspectionCorrectionService();

        var plan = service.Prepare(project, new InspectionCorrectionPackage
        {
            ReportId = report.Id,
            Corrections =
            [
                new InspectionCorrection
                {
                    SectionId = section.Id,
                    OccurrenceId = occurrence.Id,
                    Description = "TEXTO CORRIGIDO"
                },
                new InspectionCorrection
                {
                    SectionId = section.Id,
                    OccurrenceId = missingId,
                    Meterage = 20m
                }
            ]
        });
        var snapshots = service.Apply(plan);

        Assert.Single(snapshots);
        Assert.Equal("TEXTO CORRIGIDO", occurrence.EffectiveDescription);
        Assert.Single(section.Occurrences);
        var missing = Assert.Single(plan.Items.Where(x =>
            x.Status == InspectionCorrectionResolutionStatus.NotFound));
        Assert.Equal(missingId, missing.Correction.OccurrenceId);
    }

    [Fact]
    public void OmittedFieldsRemainAbsentAfterProtocolDeserialization()
    {
        var sectionId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        var json = $$"""
        {
          "reportId": "{{Guid.NewGuid()}}",
          "corrections": [
            {
              "sectionId": "{{sectionId}}",
              "occurrenceId": "{{occurrenceId}}",
              "meterage": 13.75
            }
          ]
        }
        """;

        var package = InspectionPipeProtocol.DeserializePayload<InspectionCorrectionPackage>(
            Encoding.UTF8.GetBytes(json));
        var correction = Assert.Single(package.Corrections);

        Assert.True(correction.HasMeterage);
        Assert.False(correction.HasDescription);
        Assert.Equal(13.75m, correction.Meterage);
    }

    [Fact]
    public void DuplicateOccurrenceCorrectionsAreRejected()
    {
        var (project, report, section, occurrence) = NewProject();
        var package = new InspectionCorrectionPackage
        {
            ReportId = report.Id,
            Corrections =
            [
                new InspectionCorrection
                {
                    SectionId = section.Id,
                    OccurrenceId = occurrence.Id,
                    Meterage = 2m
                },
                new InspectionCorrection
                {
                    SectionId = section.Id,
                    OccurrenceId = occurrence.Id,
                    Description = "Duplicada"
                }
            ]
        };

        var plan = new InspectionCorrectionService().Prepare(project, package);

        Assert.Equal(2, plan.InvalidCount);
        Assert.All(plan.Items, x => Assert.Equal(
            InspectionCorrectionResolutionStatus.Invalid, x.Status));
        Assert.Empty(new InspectionCorrectionService().Apply(plan));
        Assert.Equal(1m, occurrence.Meterage);
        Assert.Equal("ORIGINAL", occurrence.EffectiveDescription);
    }

    private static (Project Project, Report Report, InspectionSection Section, Occurrence Occurrence)
        NewProject()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Projeto" };
        var location = new Location { ProjectId = project.Id, Name = "PV 07" };
        var section = new InspectionSection { LocationId = location.Id, Name = "PV 07 - PV 08" };
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            LocationMode = OccurrenceLocationMode.Point,
            Meterage = 1m,
            GeneratedDescription = "ORIGINAL"
        };
        section.Occurrences.Add(occurrence);
        location.Sections.Add(section);
        project.Locations.Add(location);
        var report = project.CreateReport();
        report.SelectSection(section);
        return (project, report, section, occurrence);
    }
}
