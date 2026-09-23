using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class PendingCaptureRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "GeoAutoDocsPendingRemovalTests", Guid.NewGuid().ToString("N"));
    private readonly Project _project;
    private readonly InspectionSection _section;
    private readonly FrameCaptureService _service;

    public PendingCaptureRemovalTests()
    {
        Directory.CreateDirectory(_root);
        _project = new Project { Name = "Fixture", ProjectPath = Path.Combine(_root, "project") };
        var location = new Location { ProjectId = _project.Id, Name = "PV 01" };
        _section = new InspectionSection { LocationId = location.Id, Name = "Trecho 01" };
        location.Sections.Add(_section);
        _project.Locations.Add(location);
        _service = new FrameCaptureService(new AppDataPaths(Path.Combine(_root, "app")),
            new LocalLogger(Path.Combine(_root, "test.log")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CandidatesAreRestrictedToSnapshotSectionProjectAndPendingStatus()
    {
        var expected = Capture("expected");
        var newer = Capture("newer");
        var assigned = Capture("assigned"); assigned.Status = CaptureStatus.Assigned;
        var otherSection = Capture("other-section"); otherSection.SectionId = Guid.NewGuid();
        var otherProject = Capture("other-project"); otherProject.ProjectId = Guid.NewGuid();

        var candidates = PendingCaptureRemoval.FindRemovable(_project, _section.Id,
            [expected.Id, assigned.Id, otherSection.Id, otherProject.Id]);

        Assert.Same(expected, Assert.Single(candidates));
        Assert.Contains(newer, _project.Captures);
    }

    [Fact]
    public void EvidenceCaptureIdProtectsPendingCaptureEvenWithStaleStatus()
    {
        var capture = Capture("linked");
        LinkEvidence(capture, includeCaptureId: true, samePath: false);

        Assert.Empty(Candidates(capture));
    }

    [Fact]
    public void EvidencePathProtectsCaptureWithoutCaptureId()
    {
        var capture = Capture("linked");
        LinkEvidence(capture, includeCaptureId: false, samePath: true);

        Assert.Empty(Candidates(capture));
    }

    [Fact]
    public async Task FailedSaveRestoresIdentityOrderAndDoesNotTouchPng()
    {
        var first = Capture("first");
        var removed = Capture("removed");
        var last = Capture("last");
        var original = _project.Captures.ToArray();
        var saves = 0;

        var success = await PendingCaptureRemoval.RemoveAsync(_project, Candidates(removed), () =>
        {
            saves++;
            Assert.DoesNotContain(removed, _project.Captures);
            Assert.True(File.Exists(removed.FilePath));
            return Task.FromResult(false);
        });

        Assert.False(success);
        Assert.Equal(1, saves);
        Assert.Equal(original, _project.Captures);
        Assert.Same(first, _project.Captures[0]);
        Assert.Same(removed, _project.Captures[1]);
        Assert.Same(last, _project.Captures[2]);
        Assert.True(File.Exists(removed.FilePath));
    }

    [Fact]
    public async Task ExceptionDuringSaveAlsoRestoresReferences()
    {
        var capture = Capture("exception");

        await Assert.ThrowsAsync<IOException>(() =>
            PendingCaptureRemoval.RemoveAsync(_project, Candidates(capture),
                () => throw new IOException("Falha deliberada")));

        Assert.Same(capture, Assert.Single(_project.Captures));
        Assert.True(File.Exists(capture.FilePath));
    }

    [Fact]
    public async Task SuccessfulSaveRemovesOnlyConfirmedReferencesBeforeFileProcessing()
    {
        var removed = Capture("removed");
        var keep = Capture("keep");
        var calls = 0;

        Assert.True(await PendingCaptureRemoval.RemoveAsync(_project, Candidates(removed), () =>
        {
            calls++;
            Assert.Equal([keep], _project.Captures);
            return Task.FromResult(true);
        }));

        Assert.Equal(1, calls);
        Assert.True(File.Exists(removed.FilePath));
        Assert.True(File.Exists(keep.FilePath));
    }

    [Fact]
    public async Task AssignmentAfterConfirmationRejectsRemovalBeforeSaving()
    {
        var capture = Capture("changed");
        var candidates = Candidates(capture);
        capture.Status = CaptureStatus.Assigned;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PendingCaptureRemoval.RemoveAsync(_project, candidates,
                () => throw new InvalidOperationException("Persist should not run")));

        Assert.Contains(capture, _project.Captures);
        Assert.True(File.Exists(capture.FilePath));
    }

    [Fact]
    public async Task NewEvidenceAfterConfirmationAlsoRejectsRemoval()
    {
        var capture = Capture("new-link");
        var candidates = Candidates(capture);
        LinkEvidence(capture, includeCaptureId: true, samePath: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PendingCaptureRemoval.RemoveAsync(_project, candidates,
                () => throw new InvalidOperationException("Persist should not run")));

        Assert.Contains(capture, _project.Captures);
    }

    [Fact]
    public async Task DuplicateIdOutsideProjectDoesNotRemoveUnconfirmedObject()
    {
        var removed = Capture("confirmed");
        var keep = Capture("other-project");
        keep.Id = removed.Id;
        keep.ProjectId = Guid.NewGuid();

        Assert.True(await PendingCaptureRemoval.RemoveAsync(_project, Candidates(removed),
            () => Task.FromResult(true)));

        Assert.Same(keep, Assert.Single(_project.Captures));
        Assert.True(File.Exists(keep.FilePath));
    }

    [Fact]
    public async Task EmptyRemovalDoesNotPersist()
    {
        Assert.True(await PendingCaptureRemoval.RemoveAsync(_project, [],
            () => throw new InvalidOperationException("Persist should not run")));
    }

    [Fact]
    public async Task ManagedPngMovesToRecoveryOnlyAfterSuccessfulSave()
    {
        var capture = Capture("recoverable");
        Assert.True(await PendingCaptureRemoval.RemoveAsync(_project, Candidates(capture),
            () => Task.FromResult(true)));

        var result = _service.ArchiveRemovedPendingFiles(_project, [capture], false);

        Assert.Equal(1, result.ArchivedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.False(File.Exists(capture.FilePath));
        var recovered = Assert.Single(Directory.GetFiles(result.RecoveryDirectory!));
        Assert.Equal("fixture-recoverable", File.ReadAllText(recovered));
    }

    [Fact]
    public void ArchiveBeforeReferenceRemovalDoesNotTouchFile()
    {
        var capture = Capture("still-listed");

        var result = _service.ArchiveRemovedPendingFiles(_project, [capture], false);

        Assert.Equal(0, result.ArchivedFiles);
        Assert.Null(result.RecoveryDirectory);
        Assert.True(File.Exists(capture.FilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalOrUnmanagedImagesAreNotMoved(bool outsideManagedDirectory)
    {
        var capture = Capture("external");
        if (outsideManagedDirectory)
        {
            capture.FilePath = Path.Combine(_root, "external.png");
            File.WriteAllText(capture.FilePath, "external");
        }
        else capture.ManagedByGeoAutoDocs = false;
        _project.Captures.Remove(capture);

        var result = _service.ArchiveRemovedPendingFiles(_project, [capture], false);

        Assert.Equal(0, result.ArchivedFiles);
        Assert.True(File.Exists(capture.FilePath));
    }

    [Fact]
    public void SharedFileReferencedByRemainingCaptureIsPreserved()
    {
        var removed = Capture("shared");
        var keep = Capture("keep");
        keep.FilePath = removed.FilePath;
        _project.Captures.Remove(removed);

        var result = _service.ArchiveRemovedPendingFiles(_project, [removed], false);

        Assert.Equal(0, result.ArchivedFiles);
        Assert.True(File.Exists(removed.FilePath));
    }

    [Fact]
    public void EvidencePathProtectsPngEvenAfterMetadataRemoval()
    {
        var capture = Capture("evidence");
        _project.Captures.Remove(capture);
        LinkEvidence(capture, includeCaptureId: false, samePath: true);

        Assert.Equal(0, _service.ArchiveRemovedPendingFiles(_project, [capture], false).ArchivedFiles);
        Assert.True(File.Exists(capture.FilePath));
    }

    [Fact]
    public void ReadOnlyProjectCannotArchiveFiles()
    {
        var capture = Capture("readonly");
        _project.Captures.Remove(capture);

        Assert.Throws<InvalidOperationException>(() =>
            _service.ArchiveRemovedPendingFiles(_project, [capture], true));
        Assert.True(File.Exists(capture.FilePath));
    }

    [Fact]
    public void MissingPngDoesNotCreateRecoveryDirectory()
    {
        var capture = Capture("missing");
        _project.Captures.Remove(capture);
        File.Delete(capture.FilePath);

        var result = _service.ArchiveRemovedPendingFiles(_project, [capture], false);

        Assert.Equal(0, result.ArchivedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Null(result.RecoveryDirectory);
    }

    [Fact]
    public void LockedPngIsReportedAndKeptAtOriginalPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var capture = Capture("locked");
        _project.Captures.Remove(capture);
        using var stream = new FileStream(capture.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = _service.ArchiveRemovedPendingFiles(_project, [capture], false);

        Assert.Equal(0, result.ArchivedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.True(File.Exists(capture.FilePath));
    }

    private Capture Capture(string name)
    {
        var directory = _service.GetManagedCaptureDirectory(_project);
        Directory.CreateDirectory(directory);
        var capture = new Capture
        {
            ProjectId = _project.Id, SectionId = _section.Id,
            FilePath = Path.Combine(directory, name + ".png")
        };
        File.WriteAllText(capture.FilePath, "fixture-" + name);
        _project.Captures.Add(capture);
        return capture;
    }

    private IReadOnlyList<Capture> Candidates(Capture capture) =>
        PendingCaptureRemoval.FindRemovable(_project, _section.Id, [capture.Id]);

    private void LinkEvidence(Capture capture, bool includeCaptureId, bool samePath)
    {
        var occurrence = new Occurrence { InspectionSectionId = _section.Id };
        occurrence.Evidence.Add(new Evidence
        {
            OccurrenceId = occurrence.Id,
            CaptureId = includeCaptureId ? capture.Id : null,
            FilePath = samePath ? capture.FilePath : Path.Combine(_root, "other-evidence.png")
        });
        _section.Occurrences.Add(occurrence);
    }
}
