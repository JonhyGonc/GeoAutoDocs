using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class InspectionTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsTransportTests", Guid.NewGuid().ToString("N"));
    private readonly InspectionExchangeSerializer _serializer = new();

    public InspectionTransportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task FileTransportWritesExactlyTheOfficialSerializedContract()
    {
        var document = NewDocument();
        var expected = _serializer.Serialize(document);
        Assert.Equal(expected, _serializer.Serialize(document));
        var path = Path.Combine(_root, "report.geoadocs.json");

        var result = await new FileInspectionTransport(path, _serializer).SendAsync(document);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Equal(document.Report.ReportId, _serializer.Deserialize(expected).Report.ReportId);
    }

    [Fact]
    public async Task CompatibleHandshakePushesValidDocumentAndRequiresSuccessAck()
    {
        var pipeName = NewPipeName();
        InspectionExchangeDocument? received = null;
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await InspectionPipeProtocol.ReadFrameAsync(server);
            Assert.Equal(InspectionPipeMessageType.Hello, hello.MessageType);
            var helloData = InspectionPipeProtocol.DeserializePayload<InspectionHello>(hello.Payload);
            Assert.Equal(InspectionExchangeSchema.CurrentVersion, helloData.InspectionSchemaVersion);
            await WriteHelloAckAsync(server, hello.RequestId);
            var push = await InspectionPipeProtocol.ReadFrameAsync(server);
            Assert.Equal(InspectionPipeMessageType.InspectionPush, push.MessageType);
            received = _serializer.Deserialize(Encoding.UTF8.GetString(push.Payload));
            await InspectionPipeProtocol.WriteFrameAsync(server, InspectionPipeMessageType.InspectionAck,
                push.RequestId, InspectionPipeProtocol.SerializePayload(new InspectionAcknowledgement
                {
                    Accepted = true, Status = "Success", Message = "Inspeção validada pelo servidor de teste."
                }));
        });
        var transport = NewTransport(pipeName);

        var result = await transport.SendAsync(NewDocument());
        await serverTask;

        Assert.True(result.Success, result.Message);
        Assert.Equal("GeoAutoEngine", result.RemoteApplication);
        Assert.NotNull(received);
        Assert.Equal(NewDocument().Project.ProjectId, received!.Project.ProjectId);
    }

    [Fact]
    public async Task IncompatibleInspectionSchemaIsRejectedDuringHandshake()
    {
        var pipeName = NewPipeName();
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await InspectionPipeProtocol.ReadFrameAsync(server);
            await WriteHelloAckAsync(server, hello.RequestId, schemaVersion: 99);
        });

        var result = await NewTransport(pipeName).SendAsync(NewDocument());
        await serverTask;

        Assert.Equal(InspectionTransportStatus.IncompatibleVersion, result.Status);
        Assert.Contains("incompatível", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerErrorDoesNotProduceFalsePositive()
    {
        var pipeName = NewPipeName();
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await InspectionPipeProtocol.ReadFrameAsync(server);
            await WriteHelloAckAsync(server, hello.RequestId);
            var push = await InspectionPipeProtocol.ReadFrameAsync(server);
            await InspectionPipeProtocol.WriteFrameAsync(server, InspectionPipeMessageType.Error,
                push.RequestId, InspectionPipeProtocol.SerializePayload(new InspectionPipeError
                {
                    Status = "InvalidPayload", Message = "Documento rejeitado pelo receptor."
                }));
        });

        var result = await NewTransport(pipeName).SendAsync(NewDocument());
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(InspectionTransportStatus.InvalidPayload, result.Status);
    }

    [Fact]
    public async Task UnexpectedAckMessageDoesNotProduceFalsePositive()
    {
        var pipeName = NewPipeName();
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await InspectionPipeProtocol.ReadFrameAsync(server);
            await WriteHelloAckAsync(server, hello.RequestId);
            var push = await InspectionPipeProtocol.ReadFrameAsync(server);
            await WriteHelloAckAsync(server, push.RequestId);
        });

        var result = await NewTransport(pipeName).SendAsync(NewDocument());
        await serverTask;

        Assert.False(result.Success);
        Assert.Equal(InspectionTransportStatus.CommunicationError, result.Status);
    }

    [Fact]
    public async Task MissingServerReturnsDestinationUnavailableQuickly()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await NewTransport(NewPipeName(), connectMilliseconds: 100).SendAsync(NewDocument());

        stopwatch.Stop();
        Assert.Equal(InspectionTransportStatus.DestinationUnavailable, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Duração: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task HandshakeTimeoutDoesNotBlock()
    {
        var pipeName = NewPipeName();
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            _ = await InspectionPipeProtocol.ReadFrameAsync(server);
            await Task.Delay(300);
        });
        var stopwatch = Stopwatch.StartNew();

        var result = await NewTransport(pipeName, responseMilliseconds: 75).SendAsync(NewDocument());
        stopwatch.Stop();
        await serverTask;

        Assert.Equal(InspectionTransportStatus.Timeout, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Duração: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task CancellationTokenInterruptsPendingAcknowledgement()
    {
        var pipeName = NewPipeName();
        var pushReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await InspectionPipeProtocol.ReadFrameAsync(server);
            await WriteHelloAckAsync(server, hello.RequestId);
            _ = await InspectionPipeProtocol.ReadFrameAsync(server);
            pushReceived.SetResult();
            await Task.Delay(300);
        });
        using var cancellation = new CancellationTokenSource();

        var sendTask = NewTransport(pipeName, responseMilliseconds: 1000)
            .SendAsync(NewDocument(), cancellation.Token);
        await pushReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await sendTask;
        await serverTask;

        Assert.Equal(InspectionTransportStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task CorrectionPushUsesExistingConnectionAndReturnsDetailedAck()
    {
        var pipeName = NewPipeName();
        var correctionRequestId = 731;
        InspectionCorrectionAcknowledgement? receivedAck = null;
        await using var server = CreateServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await InspectionPipeProtocol.ReadFrameAsync(server);
            await WriteHelloAckAsync(server, hello.RequestId);
            var inspection = await InspectionPipeProtocol.ReadFrameAsync(server);
            await InspectionPipeProtocol.WriteFrameAsync(server,
                InspectionPipeMessageType.InspectionAck, inspection.RequestId,
                InspectionPipeProtocol.SerializePayload(new InspectionAcknowledgement
                {
                    Accepted = true,
                    Status = "Success",
                    Message = "Inspeção recebida."
                }));

            var correction = new InspectionCorrectionPackage
            {
                ReportId = NewDocument().Report.ReportId,
                Corrections =
                [
                    new InspectionCorrection
                    {
                        SectionId = NewDocument().Sections[0].SectionId,
                        OccurrenceId = NewDocument().Sections[0].Occurrences[0].OccurrenceId,
                        Meterage = 13.75m
                    }
                ]
            };
            await InspectionPipeProtocol.WriteFrameAsync(server,
                InspectionPipeMessageType.CorrectionPush, correctionRequestId,
                InspectionPipeProtocol.SerializePayload(correction));

            var ackFrame = await InspectionPipeProtocol.ReadFrameAsync(server);
            Assert.Equal(InspectionPipeMessageType.CorrectionAck, ackFrame.MessageType);
            Assert.Equal(correctionRequestId, ackFrame.RequestId);
            receivedAck = InspectionPipeProtocol
                .DeserializePayload<InspectionCorrectionAcknowledgement>(ackFrame.Payload);
        });

        var transport = NewTransport(pipeName);
        transport.SetCorrectionHandler((package, _) =>
        {
            var item = Assert.Single(package.Corrections);
            Assert.True(item.HasMeterage);
            Assert.False(item.HasDescription);
            Assert.Equal(13.75m, item.Meterage);
            return Task.FromResult(new InspectionCorrectionAcknowledgement
            {
                Accepted = true,
                Status = "Partial",
                Message = "Uma correção aplicada.",
                RequestedCount = 1,
                AppliedCount = 1,
                Results =
                [
                    new InspectionCorrectionItemAcknowledgement
                    {
                        SectionId = item.SectionId,
                        OccurrenceId = item.OccurrenceId,
                        Applied = true,
                        Status = "Applied",
                        Message = "Correção aplicada."
                    }
                ]
            });
        });

        var result = await transport.SendAsync(NewDocument());
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.StopCorrectionSessionAsync();

        Assert.True(result.Success, result.Message);
        Assert.NotNull(receivedAck);
        Assert.True(receivedAck!.Accepted);
        Assert.Equal("Partial", receivedAck.Status);
        Assert.Equal(1, receivedAck.AppliedCount);
        Assert.True(Assert.Single(receivedAck.Results).Applied);
    }

    private NamedPipeInspectionTransport NewTransport(string pipeName, int connectMilliseconds = 500,
        int responseMilliseconds = 500) => new(_serializer, pipeName,
        TimeSpan.FromMilliseconds(connectMilliseconds), TimeSpan.FromMilliseconds(responseMilliseconds));

    private static NamedPipeServerStream CreateServer(string pipeName) => new(pipeName,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static Task WriteHelloAckAsync(Stream stream, int requestId,
        int schemaVersion = InspectionExchangeSchema.CurrentVersion) =>
        InspectionPipeProtocol.WriteFrameAsync(stream, InspectionPipeMessageType.HelloAck, requestId,
            InspectionPipeProtocol.SerializePayload(new InspectionHelloAck
            {
                Application = "GeoAutoEngine", ApplicationVersion = "1.0.0-test",
                ProtocolVersion = InspectionPipeProtocol.CurrentVersion,
                InspectionSchemaVersion = schemaVersion,
                CadProduct = "Civil 3D", CadVersion = "Test"
            }));

    private static string NewPipeName() => $"GeoAutoEngine.Inspection.Tests.{Guid.NewGuid():N}";

    private static InspectionExchangeDocument NewDocument()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reportId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var locationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var sectionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        return new InspectionExchangeDocument
        {
            GeneratedAtUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
            Project = new InspectionExchangeProject { ProjectId = projectId, Name = "Projeto" },
            Report = new InspectionExchangeReport { ReportId = reportId, ReportCode = "GF-RL-001", Name = "Relatório" },
            Locations = [new InspectionExchangeLocation { LocationId = locationId, Name = "Local", Order = 0 }],
            Sections =
            [
                new InspectionExchangeSection
                {
                    SectionId = sectionId, LocationId = locationId, Name = "Trecho", Order = 0,
                    Occurrences =
                    [
                        new InspectionExchangeOccurrence
                        {
                            OccurrenceId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                            LocationMode = "Point", Meterage = 17m, Order = 0
                        }
                    ]
                }
            ]
        };
    }
}
