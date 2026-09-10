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
        var originalUpdatedAt = DateTime.UtcNow.AddDays(-1);
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            VideoReferenceId = video.Id,
            LocationMode = OccurrenceLocationMode.Point,
            Meterage = 12.50m,
            GeneratedDescription = "DESLOCAMENTO DE JUNTA",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = originalUpdatedAt
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
        Assert.Equal(originalUpdatedAt, savedOccurrence.UpdatedAt);
        Assert.Equal("DESLOCAMENTO DE JUNTA", savedOccurrence.EffectiveDescription);
        Assert.Equal(video.Id, savedOccurrence.VideoReferenceId);
        Assert.Equal(evidence.Id, Assert.Single(savedOccurrence.Evidence).Id);
        Assert.Equal(video.Id, Assert.Single(savedSection.VideoReferences).Id);
        Assert.Equal(section.Id, Assert.Single(Assert.Single(recovered.Reports).SectionLinks).SectionId);
    }

    [Fact]
    public void EngineRangePayloadUpdatesStartEndAndDescription()
    {
        var (project, report, section, occurrence) = NewProject(OccurrenceLocationMode.Range);
        occurrence.Meterage = 10m;
        occurrence.EndMeterage = 20m;
        var json = $$"""
        {
          "reportId": "{{report.Id}}",
          "generatedAtUtc": "2026-09-10T12:00:00Z",
          "corrections": [
            {
              "sectionId": "{{section.Id}}",
              "occurrenceId": "{{occurrence.Id}}",
              "locationMode": "Range",
              "meterage": 11.25,
              "endMeterage": 22.5,
              "meteragePoints": [],
              "description": "INTERVALO CORRIGIDO"
            }
          ]
        }
        """;

        var package = InspectionPipeProtocol.DeserializePayload<InspectionCorrectionPackage>(
            Encoding.UTF8.GetBytes(json));
        var correction = Assert.Single(package.Corrections);
        Assert.True(correction.HasMeterage);
        Assert.True(correction.HasEndMeterage);
        Assert.True(correction.HasMeteragePoints);
        Assert.True(correction.HasDescription);

        var service = new InspectionCorrectionService();
        var plan = service.Prepare(project, package);
        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply);
        Assert.Equal(OccurrenceLocationMode.Range, item.LocationMode);
        Assert.Equal(11.25m, item.ProposedMeterage);
        Assert.Equal(22.5m, item.ProposedEndMeterage);

        var snapshots = service.Apply(plan);

        Assert.Single(snapshots);
        Assert.Equal(occurrence.Id, Assert.Single(section.Occurrences).Id);
        Assert.Equal(11.25m, occurrence.Meterage);
        Assert.Equal(22.5m, occurrence.EndMeterage);
        Assert.Equal("INTERVALO CORRIGIDO", occurrence.EffectiveDescription);
    }

    [Fact]
    public void MultiPointCorrectionPreservesPointIdentityAndRollbackRestoresValues()
    {
        var (project, report, _, occurrence) = NewProject(OccurrenceLocationMode.MultiPoint);
        occurrence.Meterage = null;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        occurrence.MeteragePoints =
        [
            new OccurrenceMeteragePoint
            {
                Id = firstId,
                OccurrenceId = occurrence.Id,
                Meterage = 2m,
                Order = 0
            },
            new OccurrenceMeteragePoint
            {
                Id = secondId,
                OccurrenceId = occurrence.Id,
                Meterage = 4m,
                Order = 1
            }
        ];

        var service = new InspectionCorrectionService();
        var plan = service.Prepare(project, new InspectionCorrectionPackage
        {
            ReportId = report.Id,
            Corrections =
            [
                new InspectionCorrection
                {
                    SectionId = occurrence.InspectionSectionId,
                    OccurrenceId = occurrence.Id,
                    LocationMode = "MultiPoint",
                    MeteragePoints = [2.75m, 5.50m],
                    Description = "PONTOS CORRIGIDOS"
                }
            ]
        });

        var snapshots = service.Apply(plan);
        var points = occurrence.MeteragePoints.OrderBy(x => x.Order).ToArray();
        Assert.Single(snapshots);
        Assert.Equal(2, points.Length);
        Assert.Equal(firstId, points[0].Id);
        Assert.Equal(secondId, points[1].Id);
        Assert.Equal(2.75m, points[0].Meterage);
        Assert.Equal(5.50m, points[1].Meterage);
        Assert.Equal("PONTOS CORRIGIDOS", occurrence.EffectiveDescription);

        service.Rollback(snapshots);

        points = occurrence.MeteragePoints.OrderBy(x => x.Order).ToArray();
        Assert.Equal(firstId, points[0].Id);
        Assert.Equal(secondId, points[1].Id);
        Assert.Equal(2m, points[0].Meterage);
        Assert.Equal(4m, points[1].Meterage);
        Assert.Equal("ORIGINAL", occurrence.EffectiveDescription);
    }

    [Fact]
    public void LocationModeMismatchIsRejectedWithoutChangingOccurrence()
    {
        var (project, report, section, occurrence) = NewProject(OccurrenceLocationMode.Point);
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
                    LocationMode = "Range",
                    Meterage = 2m,
                    EndMeterage = 3m,
                    Description = "NÃO DEVE APLICAR"
                }
            ]
        });

        var item = Assert.Single(plan.Items);
        Assert.Equal(InspectionCorrectionResolutionStatus.Invalid, item.Status);
        Assert.Contains("LocationMode", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Apply(plan));
        Assert.Equal(1m, occurrence.Meterage);
        Assert.Equal("ORIGINAL", occurrence.EffectiveDescription);
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
        var missing = Assert.Single(plan.Items, x =>
            x.Status == InspectionCorrectionResolutionStatus.NotFound);
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
        Assert.False(correction.HasEndMeterage);
        Assert.False(correction.HasMeteragePoints);
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

    [Fact]
    public void AcknowledgementCountsAppliedAndUnchangedAsConfirmed()
    {
        var acknowledgement = new InspectionCorrectionAcknowledgement
        {
            Accepted = true,
            Status = "Partial",
            RequestedCount = 3,
            AppliedCount = 1,
            Results =
            [
                new InspectionCorrectionItemAcknowledgement { Applied = true, Status = "Applied" },
                new InspectionCorrectionItemAcknowledgement { Applied = false, Status = "Unchanged" },
                new InspectionCorrectionItemAcknowledgement { Applied = false, Status = "Invalid" }
            ]
        };

        Assert.Equal(2, acknowledgement.AppliedCount);
        Assert.Equal(1, acknowledgement.RejectedCount);
        var json = Encoding.UTF8.GetString(InspectionPipeProtocol.SerializePayload(acknowledgement));
        Assert.Contains("\"appliedCount\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"rejectedCount\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AllUnchangedAcknowledgementIsFullyConfirmedForEngine()
    {
        var acknowledgement = new InspectionCorrectionAcknowledgement
        {
            Accepted = true,
            Status = "Success",
            RequestedCount = 2,
            AppliedCount = 0,
            Results =
            [
                new InspectionCorrectionItemAcknowledgement { Status = "Unchanged" },
                new InspectionCorrectionItemAcknowledgement { Status = "Unchanged" }
            ]
        };

        Assert.Equal(2, acknowledgement.AppliedCount);
        Assert.Equal(0, acknowledgement.RejectedCount);
    }

    private static (Project Project, Report Report, InspectionSection Section, Occurrence Occurrence)
        NewProject(OccurrenceLocationMode mode = OccurrenceLocationMode.Point)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Projeto" };
        var location = new Location { ProjectId = project.Id, Name = "PV 07" };
        var section = new InspectionSection { LocationId = location.Id, Name = "PV 07 - PV 08" };
        var occurrence = new Occurrence
        {
            InspectionSectionId = section.Id,
            LocationMode = mode,
            Meterage = mode is OccurrenceLocationMode.Point or OccurrenceLocationMode.From ? 1m : null,
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
