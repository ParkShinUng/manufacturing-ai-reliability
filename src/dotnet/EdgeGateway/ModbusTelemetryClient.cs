using System.Buffers.Binary;
using System.Net.Sockets;

namespace Mair.EdgeGateway;

/// <summary>A Modbus response the gateway cannot use: an exception PDU, or a frame that does not match its request.</summary>
public sealed class ModbusException(string message, byte? exceptionCode = null) : IOException(message)
{
    /// <summary>The Modbus exception code (§2.4) when the server sent an exception response; otherwise <c>null</c>.</summary>
    public byte? ExceptionCode { get; } = exceptionCode;
}

/// <summary>
/// Reads telemetry from the simulator's <b>read-only</b> Modbus listener.
/// <para>
/// One block read of 23 registers per equipment per poll (`OT_PROTOCOL_MAPPING.md` §2.7), never
/// seven single-register reads: the block is atomic with respect to the simulator's 100 ms update
/// cycle, so a poll cannot straddle two model steps and return a physically inconsistent sample.
/// </para>
/// <para>
/// This client has <b>no write method</b>, and the port it is given has no write path either
/// (OD-003). The gateway is read-only toward equipment by construction, not by convention.
/// </para>
/// <para>
/// <b>Why this is not NModbus (OD-006).</b> NModbus's client reads are <c>Task.Factory.StartNew</c>
/// over a blocking socket read, and twenty poll loops starved the thread pool. This reader is
/// genuinely asynchronous, speaks only FC04, keeps one request outstanding, and closes its socket on
/// any failure — so a timed-out answer can never be read as the reply to the next request.
/// </para>
/// </summary>
public sealed class ModbusTelemetryClient : IDisposable
{
    private const byte ReadInputRegisters = 0x04;
    private const int MbapLength = 7;      // transaction id, protocol id, length, unit id
    private const int MaxPduLength = 253;  // Modbus application data unit limit

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _responseTimeout;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private int _inFlight;

    public ModbusTelemetryClient(string host, int port, TimeSpan? responseTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _port = port;
        _responseTimeout = responseTimeout ?? TimeSpan.FromMilliseconds(250); // §2.7
    }

    public bool IsConnected => _stream is not null && (_tcp?.Connected ?? false);

    public int ReconnectCount { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(_host, _port, cancellationToken);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>
    /// Reconnects in place. FR-004 and R-01 require recovery **without a process restart**, so this
    /// replaces the session rather than asking the host to restart.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        ReconnectCount++;
    }

    /// <summary>Reads and decodes one equipment's telemetry block. Unit ID is <c>index + 1</c> (§2.1).</summary>
    public async Task<ModbusFrame> ReadAsync(int equipmentIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(equipmentIndex);

        // One request outstanding is what ADR-0020's amendment rests on, so it is enforced here
        // rather than trusted to every caller: a second concurrent read would interleave on the
        // stream, and which reply belongs to whom would be a guess (COD-P2-R3-001).
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)
        {
            throw new InvalidOperationException("a read is already in flight on this connection");
        }

        try
        {
            return await ExchangeAsync(equipmentIndex, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    private async Task<ModbusFrame> ExchangeAsync(int equipmentIndex, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var unitId = (byte)(equipmentIndex + 1);
        var address = (ushort)ModbusRegisterDecoder.BaseAddress(equipmentIndex);
        const ushort count = ModbusRegisterDecoder.BlockLength;
        var transactionId = ++_transactionId;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_responseTimeout);

        try
        {
            var request = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);  // protocol id: Modbus
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);  // unit + PDU
            request[6] = unitId;
            request[7] = ReadInputRegisters;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), count);
            await stream.WriteAsync(request, timeout.Token);

            var header = new byte[MbapLength];
            await stream.ReadExactlyAsync(header, timeout.Token);

            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            Require(BinaryPrimitives.ReadUInt16BigEndian(header) == transactionId, "transaction id does not match the request");
            Require(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) == 0, "protocol id is not Modbus");
            Require(header[6] == unitId, "unit id does not match the request");
            Require(length is >= 3 and <= MaxPduLength + 1, $"MBAP length {length} is out of range");

            var pdu = new byte[length - 1];
            await stream.ReadExactlyAsync(pdu, timeout.Token);

            if (pdu[0] == (ReadInputRegisters | 0x80))
            {
                Require(pdu.Length == 2, "exception response has the wrong length");
                throw new ModbusException($"unit {unitId} returned Modbus exception 0x{pdu[1]:X2}", pdu[1]);
            }

            Require(pdu[0] == ReadInputRegisters, $"function code 0x{pdu[0]:X2} does not match the request");
            Require(pdu.Length >= 2 && pdu[1] == count * 2 && pdu.Length == 2 + (count * 2),
                "byte count does not match the requested register count");

            var registers = new ushort[count];
            for (var i = 0; i < count; i++)
            {
                registers[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2 + (2 * i)));
            }

            return ModbusRegisterDecoder.Decode(registers);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request is still on the wire, and its answer may yet arrive. Closing is the only
            // way to be sure it is never read as the reply to the next request.
            Disconnect();
            throw new TimeoutException($"no response from unit {unitId} within {_responseTimeout.TotalMilliseconds} ms");
        }
        catch (ModbusException ex) when (ex.ExceptionCode is not null)
        {
            // A well-framed exception response: the stream is exactly where it should be, and the
            // connection is still good for the next request.
            throw;
        }
        catch
        {
            // Any other failure leaves the stream at an unknown position; it cannot be trusted again.
            Disconnect();
            throw;
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new ModbusException($"malformed Modbus response: {what}");
        }
    }

    private void Disconnect()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Disconnect();
}
