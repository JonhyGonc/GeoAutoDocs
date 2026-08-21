using System.Text;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed class FileInspectionTransport(string destinationPath, InspectionExchangeSerializer serializer)
    : IInspectionTransport
{
    public string DestinationPath { get; } = Path.GetFullPath(destinationPath);

    public async Task<InspectionTransportResult> SendAsync(InspectionExchangeDocument document,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = serializer.Serialize(document);
            var temporary = DestinationPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DestinationPath)!);
                await File.WriteAllTextAsync(temporary, payload, new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, DestinationPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return new InspectionTransportResult(InspectionTransportStatus.Success,
                "Arquivo de intercâmbio exportado com sucesso.", Destination: DestinationPath);
        }
        catch (OperationCanceledException)
        {
            return new InspectionTransportResult(InspectionTransportStatus.Cancelled,
                "A exportação do arquivo foi cancelada.", Destination: DestinationPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return new InspectionTransportResult(InspectionTransportStatus.InvalidPayload,
                ex.Message, Destination: DestinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InspectionTransportResult(InspectionTransportStatus.CommunicationError,
                $"Não foi possível gravar o arquivo de intercâmbio: {ex.Message}", Destination: DestinationPath);
        }
    }

    public static string GetSidecarPath(string docxPath) =>
        Path.ChangeExtension(Path.GetFullPath(docxPath), ".geoadocs.json");
}
