using System.Net.Sockets;
using NModbus;

namespace Mair.EdgeGateway;

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
/// </summary>
public sealed class ModbusTelemetryClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _responseTimeout;

    private TcpClient? _tcp;
    private IModbusMaster? _master;

    public ModbusTelemetryClient(string host, int port, TimeSpan? responseTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _port = port;
        _responseTimeout = responseTimeout ?? TimeSpan.FromMilliseconds(250); // §2.7
    }

    public bool IsConnected => _tcp?.Connected ?? false;

    public int ReconnectCount { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();

        var tcp = new TcpClient();
        await tcp.ConnectAsync(_host, _port, cancellationToken);

        tcp.ReceiveTimeout = (int)_responseTimeout.TotalMilliseconds;
        tcp.SendTimeout = (int)_responseTimeout.TotalMilliseconds;

        _tcp = tcp;
        _master = new ModbusFactory().CreateMaster(tcp);
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

        var master = _master ?? throw new InvalidOperationException("not connected");
        var unitId = (byte)(equipmentIndex + 1);
        var baseAddress = (ushort)ModbusRegisterDecoder.BaseAddress(equipmentIndex);

        var registers = await master
            .ReadInputRegistersAsync(unitId, baseAddress, ModbusRegisterDecoder.BlockLength)
            .WaitAsync(_responseTimeout, cancellationToken);

        return ModbusRegisterDecoder.Decode(registers);
    }

    private void Disconnect()
    {
        _master?.Dispose();
        _tcp?.Dispose();
        _master = null;
        _tcp = null;
    }

    public void Dispose() => Disconnect();
}
