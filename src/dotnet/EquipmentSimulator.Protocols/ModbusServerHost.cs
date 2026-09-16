using System.Net;
using System.Net.Sockets;
using NModbus;

namespace Mair.EquipmentSimulator.Protocols;

/// <summary>
/// Hosts the simulator's two Modbus TCP listeners (`OT_PROTOCOL_MAPPING.md` §2.1, OD-003).
/// <para>
/// <b>5020 is read-only</b> and is the Edge Gateway's endpoint: its data store has no write path,
/// so every write function code is refused with exception <c>0x01</c>. <b>5021</b> accepts the one
/// documented write and belongs to the Control Service. Modbus TCP carries no identity, so this
/// separation is the only way "the gateway cannot write" can be enforced by the server instead of
/// trusted of the client.
/// </para>
/// <para>
/// The split does <b>not</b> protect against a gateway misconfigured to 5021 — that is network
/// policy's job. What it buys is that a gateway on 5020 cannot write whatever defect or compromise
/// it suffers, because the connection it holds carries no write function codes.
/// </para>
/// </summary>
public sealed class ModbusServerHost : IAsyncDisposable
{
    public const int ReadOnlyPort = 5020;
    public const int WritePort = 5021;

    private readonly TcpListener _readOnlyListener;
    private readonly TcpListener _writeListener;
    private readonly IModbusSlaveNetwork _readOnlyNetwork;
    private readonly IModbusSlaveNetwork _writeNetwork;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _readOnlyLoop;
    private Task? _writeLoop;

    /// <param name="equipment">The simulated equipment this server exposes.</param>
    /// <param name="readOnlyPort">Overridable so tests can bind an ephemeral port.</param>
    /// <param name="writePort">Overridable so tests can bind an ephemeral port.</param>
    public ModbusServerHost(IEquipmentAccess equipment, int readOnlyPort = ReadOnlyPort, int writePort = WritePort)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var factory = new ModbusFactory();

        _readOnlyListener = new TcpListener(IPAddress.Loopback, readOnlyPort);
        _writeListener = new TcpListener(IPAddress.Loopback, writePort);
        _readOnlyListener.Start();
        _writeListener.Start();

        _readOnlyNetwork = factory.CreateSlaveNetwork(_readOnlyListener);
        _writeNetwork = factory.CreateSlaveNetwork(_writeListener);

        // Unit ID = equipmentIndex + 1 (§2.1), so unit 0 — the Modbus broadcast address — is never
        // a valid equipment and a broadcast cannot address a machine by accident.
        for (var index = 0; index < equipment.Count; index++)
        {
            var unitId = (byte)(index + 1);
            _readOnlyNetwork.AddSlave(factory.CreateSlave(unitId, new ModbusTelemetryDataStore(equipment, writable: false)));
            _writeNetwork.AddSlave(factory.CreateSlave(unitId, new ModbusTelemetryDataStore(equipment, writable: true)));
        }
    }

    public int ActualReadOnlyPort => ((IPEndPoint)_readOnlyListener.LocalEndpoint).Port;

    public int ActualWritePort => ((IPEndPoint)_writeListener.LocalEndpoint).Port;

    public void Start()
    {
        _readOnlyLoop ??= _readOnlyNetwork.ListenAsync(_stopping.Token);
        _writeLoop ??= _writeNetwork.ListenAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var loop in new[] { _readOnlyLoop, _writeLoop })
        {
            try
            {
                if (loop is not null)
                {
                    await loop;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the listener is stopped.
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                // The socket is torn down under the listener during shutdown; NModbus surfaces that
                // as an ordinary I/O failure. Swallowed only while stopping, never while running.
            }
        }

        _readOnlyNetwork.Dispose();
        _writeNetwork.Dispose();
        _readOnlyListener.Dispose();
        _writeListener.Dispose();
        _stopping.Dispose();
    }
}
