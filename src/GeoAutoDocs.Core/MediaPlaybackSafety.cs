using System.Buffers.Binary;

namespace GeoAutoDocs.Core;

public enum MediaPreflightFailureKind
{
    None = 0,
    Missing = 1,
    Empty = 2,
    InvalidContainer = 3
}

public sealed record MediaPreflightResult(
    bool CanOpen, long FileLength, MediaPreflightFailureKind FailureKind, string? FailureReason)
{
    public static MediaPreflightResult Accepted(long fileLength) =>
        new(true, fileLength, MediaPreflightFailureKind.None, null);

    public static MediaPreflightResult Rejected(
        long fileLength, MediaPreflightFailureKind failureKind, string reason) =>
        new(false, fileLength, failureKind, reason);
}

public static class MediaPlaybackSafety
{
    private static readonly byte[] RiffSignature = "RIFF"u8.ToArray();
    private static readonly byte[] AviSignature = "AVI "u8.ToArray();

    public static IReadOnlyList<string> LibVlcOptions { get; } =
    [
        "--no-video-title-show",
        "--no-audio",
        "--avcodec-hw=none",
        "--file-caching=1000",
        "--network-caching=1500"
    ];

    public static async Task<MediaPreflightResult> InspectAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var information = new FileInfo(path);
        if (!information.Exists)
            return MediaPreflightResult.Rejected(0, MediaPreflightFailureKind.Missing,
                "O arquivo não foi encontrado ou ainda não está disponível.");
        if (information.Length <= 0)
            return MediaPreflightResult.Rejected(0, MediaPreflightFailureKind.Empty, "O arquivo está vazio.");

        if (!string.Equals(information.Extension, ".avi", StringComparison.OrdinalIgnoreCase))
            return MediaPreflightResult.Accepted(information.Length);

        if (information.Length < 12)
            return MediaPreflightResult.Rejected(information.Length, MediaPreflightFailureKind.InvalidContainer,
                "O cabeçalho AVI está incompleto.");

        var header = new byte[12];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(offset), cancellationToken);
            if (read == 0)
                return MediaPreflightResult.Rejected(information.Length, MediaPreflightFailureKind.InvalidContainer,
                    "O cabeçalho AVI terminou inesperadamente.");
            offset += read;
        }

        if (!header.AsSpan(0, 4).SequenceEqual(RiffSignature) ||
            !header.AsSpan(8, 4).SequenceEqual(AviSignature))
            return MediaPreflightResult.Rejected(information.Length, MediaPreflightFailureKind.InvalidContainer,
                "A assinatura RIFF/AVI é inválida.");

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (declaredSize < 4)
            return MediaPreflightResult.Rejected(information.Length, MediaPreflightFailureKind.InvalidContainer,
                "O tamanho declarado no AVI é inválido.");

        return MediaPreflightResult.Accepted(information.Length);
    }
}
