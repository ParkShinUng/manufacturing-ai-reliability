using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sim = Mair.EquipmentSimulator;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// The gateway's own Modbus FC04 reader (OD-006), against a server that misbehaves on purpose.
/// <para>
/// ADR-0020 rejected hand-rolling Modbus for the risks it named: transaction ids, partial and
/// over-length frames, exception framing. The amendment that allowed this reader rests on those
/// being tested in isolation, and this is where they are. The healthy path is covered separately,
/// on the wire against the simulator's independent NModbus server.
/// </para>
/// </summary>
public sealed class ModbusFramingTests
{
    /// <summary>What the scripted server does with request number <c>n</c> (0-based, per connection).</summary>
    private delegate Task Script(NetworkStream stream, byte[] request, int n);

    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _accepting;

        public ScriptedServer(Script script)
        {
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using var _ = client;
                        var stream = client.GetStream();
                        try
                        {
                            for (var n = 0; ; n++)
                            {
                                var request = new byte[12];
                                await stream.ReadExactlyAsync(request, _stopping.Token);
                                Requests.Add(request);
                                await script(stream, request, n);
                            }
                        }
                        catch (Exception)
                        {
                            // The client closed the connection, which several tests require it to.
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<byte[]> Requests { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _accepting;
        }
    }

    private static readonly ushort[] Block = Sim.ModbusRegisterEncoder.Encode(new Sim.RawSample
    {
        EquipmentId = "eq-001",
        Sequence = 42,
        SourceEpochMs = 4_200,
        State = Sim.EquipmentState.Running,
        Rpm = 1500,
        TorqueNm = 12.5,
        CurrentA = 18.2,
        VoltageV = 400,
        TemperatureC = 60,
        VibrationRms = 2.5,
        OperationRatePct = 80,
        Flags = [],
        RawQuality = Sim.QualityOverall.Good,
    });

    /// <summary>A correct FC04 response to <paramref name="request"/>, with fields optionally broken.</summary>
    private static byte[] Response(
        byte[] request, ushort? transactionId = null, byte? unit = null, byte function = 0x04,
        int? byteCount = null, int? mbapLength = null, ushort[]? registers = null, ushort protocolId = 0)
    {
        registers ??= Block;
        var pdu = new byte[2 + (registers.Length * 2)];
        pdu[0] = function;
        pdu[1] = (byte)(byteCount ?? registers.Length * 2);
        for (var i = 0; i < registers.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (2 * i)), registers[i]);
        }

        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId ?? BinaryPrimitives.ReadUInt16BigEndian(request));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), protocolId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(mbapLength ?? pdu.Length + 1));
        frame[6] = unit ?? request[6];
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private static async Task<ModbusTelemetryClient> ConnectedAsync(ScriptedServer server)
    {
        var client = new ModbusTelemetryClient("127.0.0.1", server.Port);
        await client.ConnectAsync();
        return client;
    }

    [Fact]
    public async Task TheRequestIsExactlyOneFc04BlockReadForTheEquipment()
    {
        await using var server = new ScriptedServer((s, r, _) => s.WriteAsync(Response(r)).AsTask());
        using var client = await ConnectedAsync(server);

        await client.ReadAsync(2);

        var request = Assert.Single(server.Requests);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2))); // protocol id
        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4))); // length
        Assert.Equal(3, request[6]);                                               // unit = index + 1
        Assert.Equal(0x04, request[7]);                                            // FC04, never a write
        Assert.Equal(80, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8))); // base = 40 * index
        Assert.Equal(23, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
    }

    [Fact]
    public async Task AResponseTrickledOneByteAtATimeStillDecodes()
    {
        await using var server = new ScriptedServer(async (s, r, _) =>
        {
            foreach (var b in Response(r))
            {
                await s.WriteAsync(new[] { b });
                await s.FlushAsync();
                await Task.Delay(1);
            }
        });
        using var client = new ModbusTelemetryClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(5));
        await client.ConnectAsync();

        var frame = await client.ReadAsync(0);

        Assert.Equal(42UL, frame.Sequence);
        Assert.Equal(4_200u, frame.SourceEpochMs);
    }

    public static TheoryData<string> MalformedResponses =>
        ["transaction id", "protocol id", "unit id", "function code", "byte count", "mbap length too long", "mbap length too short"];

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task AMalformedResponseIsRejected_AndTheConnectionIsClosed(string defect)
    {
        await using var server = new ScriptedServer((s, r, _) =>
        {
            var tid = BinaryPrimitives.ReadUInt16BigEndian(r);
            var bytes = defect switch
            {
                "transaction id" => Response(r, transactionId: (ushort)(tid + 1)),
                "protocol id" => Response(r, protocolId: 1),
                "unit id" => Response(r, unit: (byte)(r[6] + 1)),
                "function code" => Response(r, function: 0x03),
                "byte count" => Response(r, byteCount: 44),
                "mbap length too long" => Response(r, mbapLength: 300),
                _ => Response(r, mbapLength: 2),
            };
            return s.WriteAsync(bytes).AsTask();
        });
        using var client = await ConnectedAsync(server);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadAsync(0));

        Assert.Null(ex.ExceptionCode);
        Assert.False(client.IsConnected, "a stream at an unknown position must not be reused");
    }

    [Fact]
    public async Task AnExceptionResponseCarriesItsCode_AndTheConnectionSurvives()
    {
        // 0x04 is what the simulator returns when the equipment has no data (§2.4). The frame is
        // well formed, so the connection is still good.
        await using var server = new ScriptedServer((s, r, n) =>
        {
            byte[] bytes = n == 0
                ? [r[0], r[1], 0, 0, 0, 3, r[6], 0x84, 0x04]
                : Response(r);
            return s.WriteAsync(bytes).AsTask();
        });
        using var client = await ConnectedAsync(server);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadAsync(0));

        Assert.Equal((byte)0x04, ex.ExceptionCode);
        Assert.True(client.IsConnected);
        Assert.Equal(42UL, (await client.ReadAsync(0)).Sequence);
    }

    [Fact]
    public async Task ASecondConcurrentReadIsRefused_AndTheFirstStillGetsItsOwnReply()
    {
        // COD-P2-R3-001: one request outstanding is enforced, not assumed.
        await using var server = new ScriptedServer(async (s, r, _) =>
        {
            await Task.Delay(200);
            await s.WriteAsync(Response(r));
        });
        using var client = new ModbusTelemetryClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(5));
        await client.ConnectAsync();

        var first = client.ReadAsync(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadAsync(0));

        Assert.Equal(42UL, (await first).Sequence);
        Assert.Single(server.Requests); // the refused read never reached the wire
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task ASilentServerTimesOut_AndTheConnectionIsClosed()
    {
        // A finite delay, not Timeout.Infinite: an infinite delay has no timer behind it, so nothing
        // roots the handler holding the server's socket, and a GC finalises the socket mid-test -
        // the client then sees a reset, not silence. It failed that way under the full suite only.
        await using var server = new ScriptedServer((_, _, _) => Task.Delay(TimeSpan.FromSeconds(10)));
        using var client = await ConnectedAsync(server);

        var started = Stopwatch.GetTimestamp();
        await Assert.ThrowsAsync<TimeoutException>(() => client.ReadAsync(0));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ALateAnswerIsNeverReadAsTheReplyToTheNextRequest()
    {
        // The defect OD-006 named in NModbus: WaitAsync abandoned the await, not the read. Here the
        // first request is answered after the timeout, with a sequence that would be wrong for the
        // second; the second must see only its own answer.
        var stale = (ushort[])Block.Clone();
        stale[16] = 0xFFFF; // sequence high word: a value no real reply carries

        var first = 1;
        await using var server = new ScriptedServer(async (s, r, n) =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                await Task.Delay(400);
                await s.WriteAsync(Response(r, registers: stale));
                return;
            }

            await s.WriteAsync(Response(r));
        });
        using var client = await ConnectedAsync(server);

        await Assert.ThrowsAsync<TimeoutException>(() => client.ReadAsync(0));
        await Task.Delay(300); // the late answer is now in flight on the old connection

        // Without a reconnect there is nothing to read it from: the timed-out connection is gone,
        // so the next read cannot consume the stale reply as its own.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadAsync(0));

        await client.ReconnectAsync();
        Assert.Equal(42UL, (await client.ReadAsync(0)).Sequence);
    }
}
