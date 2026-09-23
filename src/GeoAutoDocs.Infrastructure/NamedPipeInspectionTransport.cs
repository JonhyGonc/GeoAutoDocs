using System.IO.Pipes;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.Infrastructure;

public sealed class NamedPipeInspectionTransport : IInspectionTransport, IInspectionAvailabilityProbe, IInspectionCorrectionChannel
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CorrectionReconnectDelay = TimeSpan.FromSeconds(2);
    private readonly InspectionExchangeSerializer _serializer;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly LocalLogger? _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _correctionSessionGate = new(1, 1);
    private readonly CancellationTokenSource _correctionLifetime = new();
    private Func<InspectionCorrectionPackage, CancellationToken, Task<InspectionCorrectionAcknowledgement>>? _correctionHandler;
    private CorrectionSession? _correctionSession;
    private Task? _correctionReconnectTask;
    private bool _stopped;

    public string PipeName { get; }

    public NamedPipeInspectionTransport(InspectionExchangeSerializer serializer,
        string pipeName = InspectionPipeProtocol.DefaultPipeName,
        TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null,
        LocalLogger? logger = null)
    {
        _serializer = serializer;
        PipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("O nome do Named Pipe é obrigatório.", nameof(pipeName))
            : pipeName.Trim();
        _connectTimeout = ValidateTimeout(connectTimeout ?? DefaultConnectTimeout, nameof(connectTimeout));
        _responseTimeout = ValidateTimeout(responseTimeout ?? DefaultResponseTimeout, nameof(responseTimeout));
        _logger = logger;
    }

    public async Task<InspectionTransportResult> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var activeRemote = await GetActiveCorrectionRemoteAsync(cancellationToken);
        if (activeRemote is not null)
            return RemoteResult(InspectionTransportStatus.Success,
                "GeoAutoEngine disponível e canal de correções conectado.", activeRemote);

        var handshake = await ConnectAndHandshakeAsync(cancellationToken);
        handshake.Pipe?.Dispose();
        return handshake.Result;
    }

    public async Task<InspectionTransportResult> SendAsync(InspectionExchangeDocument document,
        CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
                return Result(InspectionTransportStatus.Cancelled,
                    "O canal com o GeoAutoEngine já foi encerrado.");
            return await SendCoreAsync(document, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<InspectionTransportResult> SendCoreAsync(InspectionExchangeDocument document,
        CancellationToken cancellationToken)
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

        // O GeoAutoEngine aceita uma conexão ativa por vez. Se o canal persistente de
        // correções já estiver conectado, libere-o antes de abrir a conexão usada para
        // reenviar a inspeção. Após o InspectionAck esta nova conexão volta a ser o canal
        // persistente de correções.
        await StopCurrentCorrectionSessionAsync();

        var handshake = await ConnectAndHandshakeAsync(cancellationToken);
        if (!handshake.Result.Success || handshake.Pipe is null) return handshake.Result;
        var pipe = handshake.Pipe;
        var remote = handshake.Ack!;
        var transferredToCorrectionSession = false;
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

            if (_correctionHandler is not null)
                transferredToCorrectionSession =
                    await StartCorrectionSessionAsync(pipe, remote, cancellationToken);

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
        finally
        {
            if (!transferredToCorrectionSession) pipe.Dispose();
        }
    }

    public void SetCorrectionHandler(
        Func<InspectionCorrectionPackage, CancellationToken, Task<InspectionCorrectionAcknowledgement>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_stopped) throw new ObjectDisposedException(nameof(NamedPipeInspectionTransport));
        _correctionHandler = handler;
        StartCorrectionReconnectLoop();
    }

    public async Task StopCorrectionSessionAsync()
    {
        if (_stopped) return;
        _stopped = true;
        _correctionLifetime.Cancel();

        await _sendGate.WaitAsync();
        try
        {
            await StopCurrentCorrectionSessionAsync();
        }
        finally
        {
            _sendGate.Release();
        }

        var reconnectTask = _correctionReconnectTask;
        if (reconnectTask is not null)
        {
            try
            {
                await reconnectTask;
            }
            catch (OperationCanceledException) when (_correctionLifetime.IsCancellationRequested)
            {
            }
        }

        _correctionLifetime.Dispose();
    }

    private void StartCorrectionReconnectLoop()
    {
        if (_correctionReconnectTask is not null || _stopped) return;
        _correctionReconnectTask = Task.Run(() => MaintainCorrectionSessionAsync(_correctionLifetime.Token));
    }

    private async Task MaintainCorrectionSessionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasActiveCorrectionSessionAsync(cancellationToken))
                {
                    await _sendGate.WaitAsync(cancellationToken);
                    try
                    {
                        if (_stopped) return;
                        if (!await HasActiveCorrectionSessionAsync(cancellationToken))
                            await TryConnectCorrectionSessionAsync(cancellationToken);
                    }
                    finally
                    {
                        _sendGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger?.Error("Falha ao restabelecer o canal de correções com o GeoAutoEngine.", ex);
            }

            try
            {
                await Task.Delay(CorrectionReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> TryConnectCorrectionSessionAsync(CancellationToken cancellationToken)
    {
        var handshake = await ConnectAndHandshakeAsync(cancellationToken);
        if (!handshake.Result.Success || handshake.Pipe is null || handshake.Ack is null)
            return false;

        var pipe = handshake.Pipe;
        var transferred = false;
        try
        {
            transferred = await StartCorrectionSessionAsync(pipe, handshake.Ack, cancellationToken);
            if (transferred)
                _logger?.Info("Canal de correções conectado automaticamente ao GeoAutoEngine.");
            return transferred;
        }
        finally
        {
            if (!transferred) pipe.Dispose();
        }
    }

    private async Task<bool> HasActiveCorrectionSessionAsync(CancellationToken cancellationToken)
    {
        await _correctionSessionGate.WaitAsync(cancellationToken);
        try
        {
            return _correctionSession is { } session &&
                   !session.Cancellation.IsCancellationRequested &&
                   session.Pipe.IsConnected &&
                   !session.ListenerTask.IsCompleted;
        }
        finally
        {
            _correctionSessionGate.Release();
        }
    }

    private async Task<InspectionHelloAck?> GetActiveCorrectionRemoteAsync(CancellationToken cancellationToken)
    {
        await _correctionSessionGate.WaitAsync(cancellationToken);
        try
        {
            var session = _correctionSession;
            return session is not null &&
                   !session.Cancellation.IsCancellationRequested &&
                   session.Pipe.IsConnected &&
                   !session.ListenerTask.IsCompleted
                ? session.Remote
                : null;
        }
        finally
        {
            _correctionSessionGate.Release();
        }
    }

    private async Task StopCurrentCorrectionSessionAsync()
    {
        CorrectionSession? session;
        await _correctionSessionGate.WaitAsync();
        try
        {
            session = _correctionSession;
            _correctionSession = null;
            StopSession(session);
        }
        finally
        {
            _correctionSessionGate.Release();
        }

        if (session is not null) await ObserveSessionEndAsync(session);
    }

    private async Task<bool> StartCorrectionSessionAsync(NamedPipeClientStream pipe,
        InspectionHelloAck remote, CancellationToken cancellationToken)
    {
        CorrectionSession? previous;
        await _correctionSessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped) return false;
            previous = _correctionSession;
            _correctionSession = null;
            StopSession(previous);
        }
        finally
        {
            _correctionSessionGate.Release();
        }

        if (previous is not null) await ObserveSessionEndAsync(previous);

        await _correctionSessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped) return false;
            var cancellation = new CancellationTokenSource();
            var session = new CorrectionSession(pipe, cancellation, remote);
            session.ListenerTask = ListenForCorrectionsAsync(session, remote);
            _correctionSession = session;
            _logger?.Info($"Canal de correções aguardando mensagens do GeoAutoEngine. " +
                          $"Aplicação='{remote.Application}'; Versão='{remote.ApplicationVersion}'.");
            return true;
        }
        finally
        {
            _correctionSessionGate.Release();
        }
    }

    private async Task ListenForCorrectionsAsync(CorrectionSession session, InspectionHelloAck remote)
    {
        try
        {
            while (!session.Cancellation.IsCancellationRequested && session.Pipe.IsConnected)
            {
                InspectionPipeFrame frame;
                try
                {
                    frame = await InspectionPipeProtocol.ReadFrameAsync(
                        session.Pipe, session.Cancellation.Token);
                }
                catch (EndOfStreamException)
                {
                    _logger?.Info("GeoAutoEngine encerrou o canal de correções.");
                    return;
                }

                if (frame.ProtocolVersion != InspectionPipeProtocol.CurrentVersion)
                {
                    await WriteCorrectionAckAsync(session.Pipe, frame.RequestId,
                        FailureAcknowledgement(0,
                            $"Protocolo incompatível: {frame.ProtocolVersion}."),
                        session.Cancellation.Token);
                    return;
                }

                if (frame.MessageType == InspectionPipeMessageType.Error)
                {
                    var error = InspectionPipeProtocol.DeserializePayload<InspectionPipeError>(frame.Payload);
                    _logger?.Error($"GeoAutoEngine encerrou o canal de correções: {error.Status} - {error.Message}",
                        new IOException(error.Message));
                    return;
                }

                if (frame.MessageType != InspectionPipeMessageType.CorrectionPush)
                {
                    await InspectionPipeProtocol.WriteFrameAsync(session.Pipe,
                        InspectionPipeMessageType.Error, frame.RequestId,
                        InspectionPipeProtocol.SerializePayload(new InspectionPipeError
                        {
                            Status = "CommunicationError",
                            Message = $"Mensagem inesperada no canal de correções: {frame.MessageType}."
                        }), session.Cancellation.Token);
                    return;
                }

                InspectionCorrectionAcknowledgement acknowledgement;
                try
                {
                    var package = InspectionPipeProtocol.DeserializePayload<InspectionCorrectionPackage>(frame.Payload);
                    var handler = _correctionHandler;
                    acknowledgement = handler is null
                        ? FailureAcknowledgement(package.Corrections?.Count ?? 0,
                            "O GeoAutoDocs não possui um manipulador de correções ativo.")
                        : await handler(package, session.Cancellation.Token);
                }
                catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    acknowledgement = FailureAcknowledgement(0,
                        $"O pacote de correções é inválido: {ex.Message}");
                    _logger?.Error("Pacote CorrectionPush inválido.", ex);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    acknowledgement = FailureAcknowledgement(0,
                        "O GeoAutoDocs falhou ao revisar ou aplicar as correções.");
                    _logger?.Error("Falha ao processar CorrectionPush.", ex);
                }

                await WriteCorrectionAckAsync(session.Pipe, frame.RequestId, acknowledgement,
                    session.Cancellation.Token);
                _logger?.Info($"CorrectionAck enviado. RequestId={frame.RequestId}; " +
                              $"Status='{acknowledgement.Status}'; Solicitadas={acknowledgement.RequestedCount}; " +
                              $"Aplicadas={acknowledgement.AppliedCount}; Origem='{remote.Application}'.");
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!session.Cancellation.IsCancellationRequested)
                _logger?.Error("Canal de correções interrompido.", ex);
        }
        finally
        {
            session.Pipe.Dispose();
        }
    }

    private static Task WriteCorrectionAckAsync(Stream stream, int requestId,
        InspectionCorrectionAcknowledgement acknowledgement, CancellationToken cancellationToken) =>
        InspectionPipeProtocol.WriteFrameAsync(stream, InspectionPipeMessageType.CorrectionAck,
            requestId, InspectionPipeProtocol.SerializePayload(acknowledgement), cancellationToken);

    private static InspectionCorrectionAcknowledgement FailureAcknowledgement(int requestedCount,
        string message) => new()
    {
        Accepted = false,
        Status = "Failure",
        Message = message,
        RequestedCount = requestedCount,
        AppliedCount = 0
    };

    private static void StopSession(CorrectionSession? session)
    {
        if (session is null) return;
        session.Cancellation.Cancel();
        session.Pipe.Dispose();
    }

    private static async Task ObserveSessionEndAsync(CorrectionSession session)
    {
        try { await session.ListenerTask; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { session.Cancellation.Dispose(); }
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

    private sealed class CorrectionSession
    {
        public CorrectionSession(
            NamedPipeClientStream pipe,
            CancellationTokenSource cancellation,
            InspectionHelloAck remote)
        {
            Pipe = pipe;
            Cancellation = cancellation;
            Remote = remote;
        }

        public NamedPipeClientStream Pipe { get; }
        public CancellationTokenSource Cancellation { get; }
        public InspectionHelloAck Remote { get; }
        public Task ListenerTask { get; set; } = Task.CompletedTask;
    }

    private sealed record HandshakeConnection(
        NamedPipeClientStream? Pipe,
        InspectionTransportResult Result,
        InspectionHelloAck? Ack);
}
