using System.IO.Pipes;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed class NamedPipeInspectionTransport : IInspectionTransport, IInspectionAvailabilityProbe
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(5);
    private readonly InspectionExchangeSerializer _serializer;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _responseTimeout;

    public string PipeName { get; }

    public NamedPipeInspectionTransport(InspectionExchangeSerializer serializer,
        string pipeName = InspectionPipeProtocol.DefaultPipeName,
        TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null)
    {
        _serializer = serializer;
        PipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("O nome do Named Pipe é obrigatório.", nameof(pipeName))
            : pipeName.Trim();
        _connectTimeout = ValidateTimeout(connectTimeout ?? DefaultConnectTimeout, nameof(connectTimeout));
        _responseTimeout = ValidateTimeout(responseTimeout ?? DefaultResponseTimeout, nameof(responseTimeout));
    }

    public async Task<InspectionTransportResult> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var handshake = await ConnectAndHandshakeAsync(cancellationToken);
        handshake.Pipe?.Dispose();
        return handshake.Result;
    }

    public async Task<InspectionTransportResult> SendAsync(InspectionExchangeDocument document,
        CancellationToken cancellationToken = default)
    {
        byte[] payload;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            payload = System.Text.Encoding.UTF8.GetBytes(_serializer.Serialize(document));
            if (payload.Length > InspectionPipeProtocol.MaxPayloadBytes)
                return Result(InspectionTransportStatus.InvalidPayload,
                    $"A inspeção excede o limite de {InspectionPipeProtocol.MaxPayloadBytes} bytes.");
        }
        catch (OperationCanceledException)
        {
            return Result(InspectionTransportStatus.Cancelled, "O envio foi cancelado.");
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return Result(InspectionTransportStatus.InvalidPayload, ex.Message);
        }

        var handshake = await ConnectAndHandshakeAsync(cancellationToken);
        if (!handshake.Result.Success || handshake.Pipe is null) return handshake.Result;
        await using var pipe = handshake.Pipe;
        var remote = handshake.Ack!;
        try
        {
            var requestId = Random.Shared.Next(1, int.MaxValue);
            await RunWithResponseTimeoutAsync(token => InspectionPipeProtocol.WriteFrameAsync(pipe,
                InspectionPipeMessageType.InspectionPush, requestId, payload, token), cancellationToken);
            var frame = await RunWithResponseTimeoutAsync(token =>
                InspectionPipeProtocol.ReadFrameAsync(pipe, token), cancellationToken);
            if (frame.ProtocolVersion != InspectionPipeProtocol.CurrentVersion)
                return RemoteResult(InspectionTransportStatus.IncompatibleVersion,
                    $"O GeoAutoEngine respondeu com protocolo {frame.ProtocolVersion}, esperado {InspectionPipeProtocol.CurrentVersion}.",
                    remote);
            if (frame.RequestId != requestId)
                return RemoteResult(InspectionTransportStatus.CommunicationError,
                    "O ACK recebido não corresponde à inspeção enviada.", remote);
            if (frame.MessageType == InspectionPipeMessageType.Error)
                return MapError(frame, remote);
            if (frame.MessageType != InspectionPipeMessageType.InspectionAck)
                return RemoteResult(InspectionTransportStatus.CommunicationError,
                    $"Resposta inesperada após o envio: {frame.MessageType}.", remote);
            var acknowledgement = InspectionPipeProtocol.DeserializePayload<InspectionAcknowledgement>(frame.Payload);
            if (!acknowledgement.Accepted)
            {
                var status = ParseRemoteStatus(acknowledgement.Status);
                return RemoteResult(status == InspectionTransportStatus.Success
                        ? InspectionTransportStatus.CommunicationError : status,
                    string.IsNullOrWhiteSpace(acknowledgement.Message)
                        ? "O GeoAutoEngine rejeitou a inspeção." : acknowledgement.Message,
                    remote);
            }
            return RemoteResult(InspectionTransportStatus.Success,
                string.IsNullOrWhiteSpace(acknowledgement.Message)
                    ? "Inspeção recebida e validada pelo GeoAutoEngine." : acknowledgement.Message,
                remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RemoteResult(InspectionTransportStatus.Cancelled, "O envio foi cancelado.", remote);
        }
        catch (TimeoutException)
        {
            return RemoteResult(InspectionTransportStatus.Timeout,
                "O GeoAutoEngine não respondeu dentro do tempo limite.", remote);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return RemoteResult(InspectionTransportStatus.CommunicationError,
                $"Falha na comunicação com o GeoAutoEngine: {ex.Message}", remote);
        }
    }

    private async Task<HandshakeConnection> ConnectAndHandshakeAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream? pipe = null;
        var connected = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)Math.Ceiling(_connectTimeout.TotalMilliseconds), cancellationToken);
            connected = true;
            var requestId = Random.Shared.Next(1, int.MaxValue);
            var hello = new InspectionHello
            {
                ApplicationVersion = typeof(NamedPipeInspectionTransport).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                InspectionSchemaVersion = InspectionExchangeSchema.CurrentVersion
            };
            await RunWithResponseTimeoutAsync(token => InspectionPipeProtocol.WriteFrameAsync(pipe,
                InspectionPipeMessageType.Hello, requestId,
                InspectionPipeProtocol.SerializePayload(hello), token), cancellationToken);
            var frame = await RunWithResponseTimeoutAsync(token =>
                InspectionPipeProtocol.ReadFrameAsync(pipe, token), cancellationToken);
            if (frame.ProtocolVersion != InspectionPipeProtocol.CurrentVersion)
                return FailAndDispose(pipe, InspectionTransportStatus.IncompatibleVersion,
                    $"Protocolo do GeoAutoEngine incompatível: {frame.ProtocolVersion}.");
            if (frame.RequestId != requestId)
                return FailAndDispose(pipe, InspectionTransportStatus.CommunicationError,
                    "A resposta do handshake possui identificador inválido.");
            if (frame.MessageType == InspectionPipeMessageType.Error)
            {
                var result = MapError(frame, null);
                pipe.Dispose();
                return new HandshakeConnection(null, result, null);
            }
            if (frame.MessageType != InspectionPipeMessageType.HelloAck)
                return FailAndDispose(pipe, InspectionTransportStatus.CommunicationError,
                    $"Resposta inesperada no handshake: {frame.MessageType}.");
            var ack = InspectionPipeProtocol.DeserializePayload<InspectionHelloAck>(frame.Payload);
            if (ack.ProtocolVersion != InspectionPipeProtocol.CurrentVersion ||
                ack.InspectionSchemaVersion != InspectionExchangeSchema.CurrentVersion)
                return FailAndDispose(pipe, InspectionTransportStatus.IncompatibleVersion,
                    $"GeoAutoEngine incompatível: protocolo {ack.ProtocolVersion}, schema de inspeção {ack.InspectionSchemaVersion}.", ack);
            return new HandshakeConnection(pipe,
                RemoteResult(InspectionTransportStatus.Success, "GeoAutoEngine disponível e compatível.", ack), ack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pipe?.Dispose();
            return new HandshakeConnection(null,
                Result(InspectionTransportStatus.Cancelled, "A comunicação foi cancelada."), null);
        }
        catch (TimeoutException)
        {
            pipe?.Dispose();
            return new HandshakeConnection(null,
                connected
                    ? Result(InspectionTransportStatus.Timeout, "O GeoAutoEngine não respondeu ao handshake.")
                    : Result(InspectionTransportStatus.DestinationUnavailable,
                        "GeoAutoEngine não encontrado. Abra o Civil 3D com o GeoAutoEngine carregado."), null);
        }
        catch (IOException ex)
        {
            pipe?.Dispose();
            return new HandshakeConnection(null,
                connected
                    ? Result(InspectionTransportStatus.CommunicationError,
                        $"Falha durante o handshake com o GeoAutoEngine: {ex.Message}")
                    : Result(InspectionTransportStatus.DestinationUnavailable,
                        "GeoAutoEngine não encontrado. Abra o Civil 3D com o GeoAutoEngine carregado."), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            pipe?.Dispose();
            return new HandshakeConnection(null,
                Result(InspectionTransportStatus.CommunicationError,
                    $"Handshake inválido: {ex.Message}"), null);
        }
    }

    private async Task RunWithResponseTimeoutAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_responseTimeout);
        try { await operation(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private async Task<T> RunWithResponseTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_responseTimeout);
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private InspectionTransportResult MapError(InspectionPipeFrame frame, InspectionHelloAck? ack)
    {
        var error = InspectionPipeProtocol.DeserializePayload<InspectionPipeError>(frame.Payload);
        var status = ParseRemoteStatus(error.Status);
        if (status == InspectionTransportStatus.Success) status = InspectionTransportStatus.CommunicationError;
        return ack is null
            ? Result(status, string.IsNullOrWhiteSpace(error.Message) ? "Erro informado pelo GeoAutoEngine." : error.Message)
            : RemoteResult(status, string.IsNullOrWhiteSpace(error.Message) ? "Erro informado pelo GeoAutoEngine." : error.Message, ack);
    }

    private static InspectionTransportStatus ParseRemoteStatus(string? status) =>
        Enum.TryParse<InspectionTransportStatus>(status, true, out var parsed)
            ? parsed : InspectionTransportStatus.CommunicationError;

    private InspectionTransportResult Result(InspectionTransportStatus status, string message) =>
        new(status, message, Destination: PipeName);

    private InspectionTransportResult RemoteResult(InspectionTransportStatus status, string message,
        InspectionHelloAck ack) => new(status, message, ack.Application, ack.ApplicationVersion,
        ack.ProtocolVersion, ack.InspectionSchemaVersion, PipeName);

    private HandshakeConnection FailAndDispose(NamedPipeClientStream pipe, InspectionTransportStatus status,
        string message, InspectionHelloAck? ack = null)
    {
        pipe.Dispose();
        return new HandshakeConnection(null, ack is null ? Result(status, message) : RemoteResult(status, message, ack), ack);
    }

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName) =>
        value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1)
            ? throw new ArgumentOutOfRangeException(parameterName, "O timeout deve estar entre zero e um minuto.")
            : value;

    private sealed record HandshakeConnection(
        NamedPipeClientStream? Pipe,
        InspectionTransportResult Result,
        InspectionHelloAck? Ack);
}
