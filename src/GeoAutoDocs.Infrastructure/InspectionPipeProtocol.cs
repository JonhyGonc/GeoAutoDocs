using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace GeoAutoDocs.Infrastructure;

public enum InspectionPipeMessageType : ushort
{
    Hello = 1,
    HelloAck = 2,
    InspectionPush = 3,
    InspectionAck = 4,
    Error = 5,
    CorrectionPush = 6,
    CorrectionAck = 7
}

public sealed record InspectionPipeFrame(
    ushort ProtocolVersion,
    InspectionPipeMessageType MessageType,
    int RequestId,
    byte[] Payload);

public static class InspectionPipeProtocol
{
    public const string DefaultPipeName = "GeoAutoEngine.Inspection";
    public const string Magic = "GADX";
    public const ushort CurrentVersion = 1;
    public const int HeaderLength = 16;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] SerializePayload<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJsonOptions);

    public static T DeserializePayload<T>(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<T>(payload, ProtocolJsonOptions)
        ?? throw new InvalidDataException("Payload do protocolo vazio ou inválido.");

    public static async Task WriteFrameAsync(Stream stream, InspectionPipeMessageType messageType,
        int requestId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default,
        ushort protocolVersion = CurrentVersion)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException($"Payload excede o limite de {MaxPayloadBytes} bytes.");
        var header = new byte[HeaderLength];
        Encoding.ASCII.GetBytes(Magic).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), protocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), (ushort)messageType);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), requestId);
        await stream.WriteAsync(header, cancellationToken);
        if (payload.Length > 0) await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<InspectionPipeFrame> ReadFrameAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            throw new InvalidDataException("Cabeçalho do Named Pipe possui identificação inválida.");
        var protocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        var messageTypeValue = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        if (!Enum.IsDefined(typeof(InspectionPipeMessageType), messageTypeValue))
            throw new InvalidDataException($"Tipo de mensagem desconhecido: {messageTypeValue}.");
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
            throw new InvalidDataException("Tamanho de payload inválido no Named Pipe.");
        var requestId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        var payload = new byte[payloadLength];
        if (payloadLength > 0) await stream.ReadExactlyAsync(payload, cancellationToken);
        return new InspectionPipeFrame(protocolVersion, (InspectionPipeMessageType)messageTypeValue,
            requestId, payload);
    }
}

public sealed class InspectionHello
{
    public string Application { get; set; } = "GeoAutoDocs";
    public string ApplicationVersion { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = InspectionPipeProtocol.CurrentVersion;
    public int InspectionSchemaVersion { get; set; }
}

public sealed class InspectionHelloAck
{
    public string Application { get; set; } = string.Empty;
    public string ApplicationVersion { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; }
    public int InspectionSchemaVersion { get; set; }
    public string? CadProduct { get; set; }
    public string? CadVersion { get; set; }
}

public sealed class InspectionAcknowledgement
{
    public bool Accepted { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class InspectionPipeError
{
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
