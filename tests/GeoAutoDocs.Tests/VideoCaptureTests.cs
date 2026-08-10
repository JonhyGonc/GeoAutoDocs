using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using System.Drawing;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class VideoCaptureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsVideoTests", Guid.NewGuid().ToString("N"));
    private AppDataPaths _paths = null!;
    private ProjectStore _store = null!;
    private LocalLogger _logger = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppDataPaths(_root);
        _store = new ProjectStore(_paths);
        _logger = new LocalLogger(_paths.LogPath);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MultipleVideosAndSameTimestampCapturesPersistWithIdentity()
    {
        var (project, section, first, second) = await CreateProjectWithVideosAsync();
        var captureDirectory = Path.Combine(project.ProjectPath!, "captures");
        Directory.CreateDirectory(captureDirectory);
        var firstPng = Path.Combine(captureDirectory, $"capture_{Guid.NewGuid():N}.png");
        var secondPng = Path.Combine(captureDirectory, $"capture_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(firstPng, TestPng);
        await File.WriteAllBytesAsync(secondPng, TestPng);
        var timestamp = TimeSpan.FromMilliseconds(138420);
        project.Captures.AddRange([
            NewCapture(project, section, first, firstPng, timestamp, 0),
            NewCapture(project, section, second, secondPng, timestamp, 1)
        ]);
        await _store.SaveAsync(project);

        var recovered = await new ProjectStore(_paths).GetAsync(project.Id);
        Assert.Equal(2, recovered!.Locations[0].Sections[0].VideoReferences.Count);
        Assert.Equal(2, recovered.Captures.Count);
        Assert.All(recovered.Captures, x => Assert.Equal(timestamp, x.VideoTimestamp));
        Assert.Equal(2, recovered.Captures.Select(x => x.VideoReferenceId).Distinct().Count());
        Assert.All(recovered.Captures, x => Assert.Equal(CaptureStatus.Pending, x.Status));
    }

    [Fact]
    public async Task RelocationPreservesVideoIdentityAndCaptureLink()
    {
        var (project, section, video, _) = await CreateProjectWithVideosAsync();
        var originalId = video.Id;
        var moved = Path.Combine(_root, "relocalizado.avi");
        await File.WriteAllBytesAsync(moved, [1, 2, 3]);
        project.Captures.Add(NewCapture(project, section, video, Path.Combine(_root, "capture.png"), TimeSpan.FromSeconds(7), 0));

        FrameCaptureService.RelocateVideo(video, moved, isReadOnly: false);

        Assert.Equal(originalId, video.Id);
        Assert.Equal(moved, video.FilePath);
        Assert.Equal(originalId, project.Captures[0].VideoReferenceId);
    }

    [Fact]
    public async Task MissingVideoFileNeverDeletesPersistedReference()
    {
        var (project, _, first, _) = await CreateProjectWithVideosAsync();
        File.Delete(first.FilePath);
        Assert.False(File.Exists(first.FilePath));
        await _store.SaveAsync(project);

        var recovered = await _store.GetAsync(project.Id);

        Assert.Contains(recovered!.Locations[0].Sections[0].VideoReferences, x => x.Id == first.Id);
        Assert.False(recovered.Locations[0].Sections[0].VideoReferences.Single(x => x.Id == first.Id).IsAvailable);
    }

    [Fact]
    public async Task ReadOnlyBlocksRelocationAcceptanceAndDeletion()
    {
        var (project, section, video, _) = await CreateProjectWithVideosAsync();
        var png = Path.Combine(project.ProjectPath!, "captures", "capture_readonly.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        await File.WriteAllBytesAsync(png, TestPng);
        var service = new FrameCaptureService(_paths, _logger);
        var draft = new FrameCaptureDraft(Guid.NewGuid(), section.Id, video.Id, png, video.FilePath,
            TimeSpan.FromSeconds(1), DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(() => service.Accept(project, draft, isReadOnly: true));
        Assert.Throws<InvalidOperationException>(() => FrameCaptureService.RelocateVideo(video, png, isReadOnly: true));
        var capture = service.Accept(project, draft, isReadOnly: false);
        Assert.Throws<InvalidOperationException>(() => service.DeletePending(project, capture, isReadOnly: true));
        Assert.Contains(capture, project.Captures);
    }

    [Fact]
    public async Task DeletingPendingCaptureRemovesManagedFileAndRecordOnly()
    {
        var (project, section, video, _) = await CreateProjectWithVideosAsync();
        var service = new FrameCaptureService(_paths, _logger);
        var directory = service.GetManagedCaptureDirectory(project);
        Directory.CreateDirectory(directory);
        var png = Path.Combine(directory, $"capture_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(png, TestPng);
        var videoBytes = await File.ReadAllBytesAsync(video.FilePath);
        var capture = NewCapture(project, section, video, png, TimeSpan.FromSeconds(2), 0);
        project.Captures.Add(capture);

        service.DeletePending(project, capture, isReadOnly: false);

        Assert.Empty(project.Captures);
        Assert.False(File.Exists(png));
        Assert.Equal(videoBytes, await File.ReadAllBytesAsync(video.FilePath));
    }

    [Fact]
    public async Task LibVlcExtractsAValidOriginalResolutionPng()
    {
        var (project, section, video, _) = await CreateProjectWithVideosAsync(createFixture: true);
        var service = new FrameCaptureService(_paths, _logger);

        var draft = await service.CaptureCurrentFrameAsync(project, section, video,
            TimeSpan.FromMilliseconds(500), isReadOnly: false);
        var dimensions = FrameCaptureService.ValidatePng(draft.FilePath);

        Assert.Equal((160, 96), dimensions);
        Assert.True(new FileInfo(draft.FilePath).Length > 100);
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            using var bitmap = new Bitmap(draft.FilePath);
            var pixel = bitmap.GetPixel(10, 10);
            Assert.True(pixel.R > 180 && pixel.B < 80,
                $"O frame não corresponde ao bloco visual esperado em 00:00:00.500: {pixel}.");
        }
        service.Discard(draft);

        var laterDraft = await service.CaptureCurrentFrameAsync(project, section, video,
            TimeSpan.FromMilliseconds(1200), isReadOnly: false);
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            using var laterBitmap = new Bitmap(laterDraft.FilePath);
            var laterPixel = laterBitmap.GetPixel(10, 10);
            Assert.True(laterPixel.B > 180 && laterPixel.R < 80,
                $"O seek de 1,2 s não retornou o bloco visual posterior esperado: {laterPixel}.");
        }
        service.Discard(laterDraft);
    }

    [Fact]
    public async Task OperationalSmokeCapturesAcrossTwoVideosAndSurvivesRestart()
    {
        var (project, section, first, second) = await CreateProjectWithVideosAsync(createFixture: true);
        var service = new FrameCaptureService(_paths, _logger);
        foreach (var (video, timestamp) in new[]
                 {
                     (first, TimeSpan.FromMilliseconds(200)),
                     (first, TimeSpan.FromMilliseconds(800)),
                     (second, TimeSpan.FromMilliseconds(1200))
                 })
        {
            var draft = await service.CaptureCurrentFrameAsync(project, section, video, timestamp, isReadOnly: false);
            service.Accept(project, draft, isReadOnly: false);
        }
        await _store.SaveAsync(project);

        var reopened = await new ProjectStore(_paths).GetAsync(project.Id);

        Assert.Equal(3, reopened!.Captures.Count);
        Assert.All(reopened.Captures, x => Assert.True(File.Exists(x.FilePath)));
        Assert.Equal(2, reopened.Captures.Count(x => x.VideoReferenceId == first.Id));
        var returnTarget = reopened.Captures.Single(x => x.VideoReferenceId == second.Id);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), returnTarget.VideoTimestamp);
        Assert.Equal(second.Id, reopened.Locations.SelectMany(x => x.Sections)
            .SelectMany(x => x.VideoReferences).Single(x => x.Id == returnTarget.VideoReferenceId).Id);
    }

    private async Task<(Project Project, InspectionSection Section, VideoReference First, VideoReference Second)>
        CreateProjectWithVideosAsync(bool createFixture = false)
    {
        var project = await _store.CreateAsync("Vídeo inspeção");
        var location = new Location { ProjectId = project.Id, Name = "Local 01" };
        var section = new InspectionSection { LocationId = location.Id, Name = "Trecho 01" };
        var firstPath = Path.Combine(_root, createFixture ? "fixture.avi" : "ausente.avi");
        if (createFixture) AviFixture.Create(firstPath, 160, 96, 20, 10);
        else await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
        var secondPath = Path.Combine(_root, "segundo.avi");
        if (createFixture) AviFixture.Create(secondPath, 160, 96, 20, 10);
        else await File.WriteAllBytesAsync(secondPath, [4, 5, 6]);
        var first = new VideoReference { InspectionSectionId = section.Id, FilePath = firstPath, DisplayName = "Principal", Order = 0 };
        var second = new VideoReference { InspectionSectionId = section.Id, FilePath = secondPath, DisplayName = "Continuação", Order = 1 };
        section.VideoReferences.AddRange([first, second]);
        location.Sections.Add(section);
        project.Locations.Add(location);
        return (project, section, first, second);
    }

    private static Capture NewCapture(Project project, InspectionSection section, VideoReference video,
        string filePath, TimeSpan timestamp, int order) => new()
    {
        ProjectId = project.Id,
        SectionId = section.Id,
        VideoReferenceId = video.Id,
        VideoFilePath = video.FilePath,
        FilePath = filePath,
        VideoTimestamp = timestamp,
        CreatedAt = DateTime.UtcNow,
        Status = CaptureStatus.Pending,
        ManagedByGeoAutoDocs = true,
        Order = order
    };

    private static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFElEQVR42mP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    private static class AviFixture
    {
        public static void Create(string path, int width, int height, int frames, int framesPerSecond)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            var rowBytes = (width * 3 + 3) & ~3;
            var frameBytes = rowBytes * height;

            var riffSize = BeginChunk(writer, "RIFF");
            FourCc(writer, "AVI ");
            var hdrlSize = BeginList(writer, "hdrl");
            WriteChunk(writer, "avih", body =>
            {
                body.Write(1_000_000 / framesPerSecond); body.Write(frameBytes * framesPerSecond);
                body.Write(0); body.Write(0x10); body.Write(frames); body.Write(0); body.Write(1);
                body.Write(frameBytes); body.Write(width); body.Write(height);
                body.Write(0); body.Write(0); body.Write(0); body.Write(0);
            });
            var strlSize = BeginList(writer, "strl");
            WriteChunk(writer, "strh", body =>
            {
                FourCc(body, "vids"); FourCc(body, "DIB "); body.Write(0); body.Write((short)0); body.Write((short)0);
                body.Write(0); body.Write(1); body.Write(framesPerSecond); body.Write(0); body.Write(frames);
                body.Write(frameBytes); body.Write(-1); body.Write(0);
                body.Write((short)0); body.Write((short)0); body.Write((short)width); body.Write((short)height);
            });
            WriteChunk(writer, "strf", body =>
            {
                body.Write(40); body.Write(width); body.Write(height); body.Write((short)1); body.Write((short)24);
                body.Write(0); body.Write(frameBytes); body.Write(0); body.Write(0); body.Write(0); body.Write(0);
            });
            EndChunk(writer, strlSize);
            EndChunk(writer, hdrlSize);

            var moviSize = BeginList(writer, "movi");
            var offsets = new List<int>();
            for (var frame = 0; frame < frames; frame++)
            {
                offsets.Add((int)(writer.BaseStream.Position - (moviSize + 4)));
                WriteChunk(writer, "00db", body => WriteFrame(body, width, height, rowBytes, frame));
            }
            EndChunk(writer, moviSize);

            WriteChunk(writer, "idx1", body =>
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    FourCc(body, "00db"); body.Write(0x10); body.Write(offsets[frame]); body.Write(frameBytes);
                }
            });
            EndChunk(writer, riffSize);
        }

        private static void WriteFrame(BinaryWriter writer, int width, int height, int rowBytes, int frame)
        {
            var firstSecond = frame < 10;
            var red = (byte)(firstSecond ? 220 : 20);
            var green = (byte)30;
            var blue = (byte)(firstSecond ? 30 : 220);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++) { writer.Write(blue); writer.Write(green); writer.Write(red); }
                for (var padding = width * 3; padding < rowBytes; padding++) writer.Write((byte)0);
            }
        }

        private static long BeginList(BinaryWriter writer, string type)
        {
            var size = BeginChunk(writer, "LIST");
            FourCc(writer, type);
            return size;
        }

        private static long BeginChunk(BinaryWriter writer, string id)
        {
            FourCc(writer, id);
            var sizePosition = writer.BaseStream.Position;
            writer.Write(0);
            return sizePosition;
        }

        private static void EndChunk(BinaryWriter writer, long sizePosition)
        {
            var end = writer.BaseStream.Position;
            var size = checked((int)(end - sizePosition - 4));
            writer.BaseStream.Position = sizePosition;
            writer.Write(size);
            writer.BaseStream.Position = end;
            if ((size & 1) != 0) writer.Write((byte)0);
        }

        private static void WriteChunk(BinaryWriter writer, string id, Action<BinaryWriter> writeBody)
        {
            var size = BeginChunk(writer, id);
            writeBody(writer);
            EndChunk(writer, size);
        }

        private static void FourCc(BinaryWriter writer, string value) => writer.Write(System.Text.Encoding.ASCII.GetBytes(value));
    }

}
