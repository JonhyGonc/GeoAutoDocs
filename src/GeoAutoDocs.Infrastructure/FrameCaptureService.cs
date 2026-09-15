using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GeoAutoDocs.Core;
using LibVLCSharp.Shared;

namespace GeoAutoDocs.Infrastructure;

public sealed record FrameCaptureDraft(
    Guid Id, Guid SectionId, Guid VideoReferenceId, string FilePath,
    string VideoFilePath, TimeSpan VideoTimestamp, DateTime CreatedAt);

public sealed record PendingCaptureArchiveResult(int ArchivedFiles, int FailedFiles, string? RecoveryDirectory);

public sealed class FrameCaptureService(AppDataPaths paths, LocalLogger logger)
{
    public async Task<FrameCaptureDraft> CaptureCurrentFrameAsync(
        Project project, InspectionSection section, VideoReference video,
        MediaPlayer mediaPlayer, bool isReadOnly, CancellationToken cancellationToken = default)
    {
        EnsureCaptureAllowed(section, video, isReadOnly);
        if (mediaPlayer.Media is null)
            throw new InvalidOperationException("Nenhum vídeo está carregado no player.");

        var id = Guid.NewGuid();
        var directory = GetManagedCaptureDirectory(project);
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, $"capture_{id:N}.png");
        var timestamp = TimeSpan.FromMilliseconds(Math.Max(0, mediaPlayer.Time));
        try
        {
            if (!mediaPlayer.TakeSnapshot(0, outputPath, 0, 0))
                throw new InvalidOperationException("O player não conseguiu capturar o quadro atualmente exibido.");

            await WaitForValidPngAsync(outputPath, cancellationToken);
            timestamp = TimeSpan.FromMilliseconds(Math.Max(0, mediaPlayer.Time));
            logger.Info($"Quadro exibido capturado de '{video.FilePath}' em {timestamp:c}: {outputPath}");
            return new FrameCaptureDraft(id, section.Id, video.Id, outputPath, video.FilePath,
                timestamp, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            logger.Error($"Falha ao capturar o quadro exibido de '{video.FilePath}' em {timestamp:c}.", ex);
            throw;
        }
    }

    public async Task<FrameCaptureDraft> CaptureCurrentFrameAsync(
        Project project, InspectionSection section, VideoReference video,
        TimeSpan timestamp, bool isReadOnly, CancellationToken cancellationToken = default)
    {
        EnsureCaptureAllowed(section, video, isReadOnly);

        var id = Guid.NewGuid();
        var directory = GetManagedCaptureDirectory(project);
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, $"capture_{id:N}.png");
        try
        {
            await ExtractFrameAtTimestampAsync(video.FilePath, timestamp, outputPath, cancellationToken);
            await WaitForValidPngAsync(outputPath, cancellationToken);
            logger.Info($"Frame capturado de '{video.FilePath}' em {timestamp:c}: {outputPath}");
            return new FrameCaptureDraft(id, section.Id, video.Id, outputPath, video.FilePath,
                timestamp, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            logger.Error($"Falha ao extrair frame de '{video.FilePath}' em {timestamp:c}.", ex);
            throw;
        }
    }

    public Capture Accept(Project project, FrameCaptureDraft draft, bool isReadOnly)
    {
        EnsureWritable(isReadOnly);
        ValidatePng(draft.FilePath);
        var capture = new Capture
        {
            Id = draft.Id,
            ProjectId = project.Id,
            SectionId = draft.SectionId,
            VideoReferenceId = draft.VideoReferenceId,
            FilePath = draft.FilePath,
            VideoFilePath = draft.VideoFilePath,
            VideoTimestamp = draft.VideoTimestamp,
            CreatedAt = draft.CreatedAt,
            Status = CaptureStatus.Pending,
            Order = project.Captures.Count == 0 ? 0 : project.Captures.Max(x => x.Order) + 1,
            ManagedByGeoAutoDocs = true
        };
        project.Captures.Add(capture);
        return capture;
    }

    public void Discard(FrameCaptureDraft? draft)
    {
        if (draft is null) return;
        TryDelete(draft.FilePath);
    }

    public void DeletePending(Project project, Capture capture, bool isReadOnly)
    {
        EnsureWritable(isReadOnly);
        if (capture.Status != CaptureStatus.Pending)
            throw new InvalidOperationException("Somente capturas pendentes podem ser excluídas nesta etapa.");
        if (!project.Captures.Remove(capture)) return;
        if (capture.ManagedByGeoAutoDocs && IsInsideManagedCaptureDirectory(project, capture.FilePath))
            TryDelete(capture.FilePath);
    }

    public PendingCaptureArchiveResult ArchiveRemovedPendingFiles(
        Project project, IReadOnlyList<Capture> captures, bool isReadOnly)
    {
        EnsureWritable(isReadOnly);
        var referencedPaths = PendingCaptureRemoval.GetReferencedPaths(project);
        var existingIds = project.Captures.Select(x => x.Id).ToHashSet();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archived = 0;
        var failed = 0;
        string? recoveryDirectory = null;
        foreach (var capture in captures)
        {
            try
            {
                var source = Path.GetFullPath(capture.FilePath);
                if (capture.Status != CaptureStatus.Pending || !capture.ManagedByGeoAutoDocs ||
                    existingIds.Contains(capture.Id) ||
                    referencedPaths.Contains(source) || !processed.Add(source) ||
                    !string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase) ||
                    !IsInsideManagedCaptureDirectory(project, source) || !File.Exists(source))
                    continue;
                recoveryDirectory ??= Path.Combine(
                    GetManagedCaptureDirectory(project), "removed", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(recoveryDirectory);
                var target = Path.Combine(recoveryDirectory, $"{capture.Id:N}_{Path.GetFileName(source)}");
                File.Move(source, target, overwrite: false);
                archived++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed++;
                logger.Error($"Captura {capture.Id} removida do projeto, mas seu PNG permaneceu no local original.", exception);
            }
        }
        return new PendingCaptureArchiveResult(archived, failed, recoveryDirectory);
    }

    public static void RelocateVideo(VideoReference video, string newPath, bool isReadOnly)
    {
        EnsureWritable(isReadOnly);
        if (string.IsNullOrWhiteSpace(newPath) || !File.Exists(newPath))
            throw new FileNotFoundException("O novo arquivo de vídeo não foi encontrado.", newPath);
        video.FilePath = Path.GetFullPath(newPath);
        if (string.IsNullOrWhiteSpace(video.DisplayName)) video.DisplayName = Path.GetFileName(newPath);
    }

    public string GetManagedCaptureDirectory(Project project) => project.StorageKind == ProjectStorageKind.Shared
        ? Path.Combine(paths.CacheDirectory, project.Id.ToString("N"), "captures")
        : Path.Combine(project.ProjectPath ?? Path.Combine(paths.ProjectsDirectory, project.Id.ToString("N")), "captures");

    private bool IsInsideManagedCaptureDirectory(Project project, string path)
    {
        var root = Path.GetFullPath(GetManagedCaptureDirectory(project))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task ExtractFrameAtTimestampAsync(string videoPath, TimeSpan timestamp, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A captura de vídeo requer Windows.");
        LibVLCSharp.Shared.Core.Initialize();
        using var libVlc = new LibVLC("--no-audio", "--no-video-title-show", "--quiet", "--avcodec-hw=none");
        using var media = new Media(libVlc, videoPath, FromType.FromPath);
        using var decoder = new MediaPlayer(libVlc);
        var synchronization = new object();
        var frameReady = new TaskCompletionSource<DecodedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        IntPtr buffer = IntPtr.Zero;
        var bufferLength = 0;
        uint decodedWidth = 0, decodedHeight = 0, decodedPitch = 0;
        var targetMilliseconds = Math.Max(0, (long)timestamp.TotalMilliseconds);
        var targetReady = false;

        MediaPlayer.LibVLCVideoFormatCb formatCallback =
            (ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines) =>
            {
                var rv32 = new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' };
                Marshal.Copy(rv32, 0, chroma, rv32.Length);
                pitches = checked(width * 4);
                lines = height;
                lock (synchronization)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    decodedWidth = width; decodedHeight = height; decodedPitch = pitches;
                    bufferLength = checked((int)(pitches * lines));
                    buffer = Marshal.AllocHGlobal(bufferLength);
                }
                return 1;
            };
        MediaPlayer.LibVLCVideoLockCb lockCallback = (_, planes) =>
        {
            lock (synchronization) Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoUnlockCb unlockCallback = (_, _, _) =>
        {
            if (!targetReady || frameReady.Task.IsCompleted || decoder.Time + 100 < targetMilliseconds) return;
            lock (synchronization)
            {
                if (buffer == IntPtr.Zero || bufferLength <= 0) return;
                var bytes = new byte[bufferLength];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                frameReady.TrySetResult(new DecodedFrame(bytes, decodedWidth, decodedHeight, decodedPitch));
            }
        };
        MediaPlayer.LibVLCVideoDisplayCb displayCallback = (_, _) => { };
        MediaPlayer.LibVLCVideoCleanupCb cleanupCallback = (ref IntPtr opaque) => { };

        decoder.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
        decoder.SetVideoFormatCallbacks(formatCallback, cleanupCallback);
        try
        {
            if (!decoder.Play(media)) throw new InvalidOperationException("O vídeo não pôde ser decodificado.");
            for (var attempt = 0; attempt < 100 && decoder.Length <= 0; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }
            if (decoder.Length <= 0) throw new InvalidDataException("Não foi possível obter a duração do vídeo.");
            targetMilliseconds = Math.Min(targetMilliseconds, Math.Max(0, decoder.Length - 1));
            decoder.Time = targetMilliseconds;
            targetReady = true;
            var frame = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            decoder.SetPause(true);
            SavePng(frame, outputPath);
        }
        finally
        {
            decoder.Stop();
            lock (synchronization)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
            GC.KeepAlive(formatCallback);
            GC.KeepAlive(lockCallback);
            GC.KeepAlive(unlockCallback);
            GC.KeepAlive(displayCallback);
            GC.KeepAlive(cleanupCallback);
        }
    }

    private static void SavePng(DecodedFrame frame, string outputPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("A codificação PNG requer Windows 7 ou posterior.");
        using var bitmap = new Bitmap((int)frame.Width, (int)frame.Height, PixelFormat.Format32bppRgb);
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            var rowBytes = checked((int)frame.Width * 4);
            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Bytes, checked((int)(row * frame.Pitch)),
                    IntPtr.Add(data.Scan0, checked((int)row * data.Stride)), rowBytes);
        }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(outputPath, ImageFormat.Png);
    }

    private static async Task WaitForValidPngAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    ValidatePng(path);
                    return;
                }
                catch (IOException) { }
                catch (InvalidDataException) { }
            }
            await Task.Delay(50, cancellationToken);
        }
        throw new InvalidDataException("O frame extraído não produziu um PNG válido.");
    }

    public static (int Width, int Height) ValidatePng(string path)
    {
        var data = File.ReadAllBytes(path);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("A captura não é um PNG válido.");
        var width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        if (width <= 0 || height <= 0) throw new InvalidDataException("A captura PNG está vazia.");
        return (width, height);
    }

    private static void EnsureWritable(bool isReadOnly)
    {
        if (isReadOnly) throw new InvalidOperationException("O projeto está em modo somente leitura.");
    }

    private static void EnsureCaptureAllowed(InspectionSection section, VideoReference video, bool isReadOnly)
    {
        EnsureWritable(isReadOnly);
        if (section.VideoReferences.All(x => x.Id != video.Id))
            throw new InvalidOperationException("O vídeo não pertence ao trecho selecionado.");
        if (!File.Exists(video.FilePath))
            throw new FileNotFoundException("Vídeo não encontrado.", video.FilePath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record DecodedFrame(byte[] Bytes, uint Width, uint Height, uint Pitch);
}
