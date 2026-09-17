using Opc.Ua;
using Opc.Ua.Client;

namespace Mair.EdgeGateway;

/// <summary>One OPC UA read of an equipment's nodes, before normalisation.</summary>
public sealed record OpcUaFrame
{
    /// <summary>Per-channel value, or <c>null</c> when the node's status is a <c>Bad_*</c> code (§1.4).</summary>
    public required IReadOnlyDictionary<Channel, double?> Values { get; init; }

    /// <summary>Flags derived from OPC UA status codes, per the §1.4 translation table.</summary>
    public required IReadOnlyList<ChannelFlag> StatusFlags { get; init; }

    public required ulong Sequence { get; init; }

    /// <summary>Milliseconds since equipment start. Added to the address space by OD-004.</summary>
    public required uint SourceEpochMs { get; init; }

    public required EquipmentState State { get; init; }

    /// <summary>OPC UA <c>SourceTimestamp</c>. Authoritative here, unlike Modbus (§2.6).</summary>
    public required DateTimeOffset SourceTimeUtc { get; init; }
}

/// <summary>
/// Reads telemetry from the simulator's OPC UA server (`OT_PROTOCOL_MAPPING.md` §1).
/// <para>
/// This client has <b>no write method</b>. The address space has exactly one writable node and the
/// gateway's session is provisioned without permission on it; not offering a write at all is the
/// client-side half of the same guarantee (FR-035).
/// </para>
/// </summary>
public sealed class OpcUaTelemetryClient : IAsyncDisposable
{
    /// <summary>Resolved by URI at session start. Hard-coding index 2 is the single most common OPC UA defect (§1.1).</summary>
    public const string NamespaceUri = "urn:mair:equipment:v1";

    private static readonly Channel[] Measurements =
    [
        Channel.Rpm, Channel.TorqueNm, Channel.CurrentA, Channel.VoltageV,
        Channel.TemperatureC, Channel.VibrationRms, Channel.OperationRatePct,
    ];

    private static readonly string[] MeasurementIdentifiers =
    [
        "Rpm", "TorqueNm", "CurrentA", "VoltageV", "TemperatureC", "VibrationRms", "OperationRatePct",
    ];

    private readonly string _endpointUrl;
    private readonly string? _pkiRoot;

    private ISession? _session;
    private ushort _namespaceIndex;

    public OpcUaTelemetryClient(string endpointUrl, string? pkiRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        _endpointUrl = endpointUrl;
        _pkiRoot = pkiRoot;
    }

    public bool IsConnected => _session?.Connected ?? false;

    public int ReconnectCount { get; private set; }

    /// <summary>The namespace index resolved from the URI. Exposed so a test can prove it was not assumed.</summary>
    public ushort NamespaceIndex => _namespaceIndex;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await CloseAsync();

        var configuration = BuildConfiguration(_pkiRoot);
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken);

        var description = await SelectEndpointAsync(configuration, _endpointUrl, cancellationToken);
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration));

        // `DefaultSessionFactory.Instance` is deprecated in 1.5.378; the telemetry-aware instance
        // is the supported path.
        var factory = new DefaultSessionFactory(DefaultTelemetry.Create(_ => { }));

        _session = await factory.CreateAsync(
            configuration,
            endpoint,
            updateBeforeConnect: false,
            sessionName: "edge-gateway",
            sessionTimeout: 60_000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null,
            ct: cancellationToken);

        // §1.1: resolve by URI, never hard-code. GetIndex returns -1 when absent, and that must be
        // a hard failure - a gateway that silently fell back to an index would read whatever
        // happened to live there.
        var index = _session.NamespaceUris.GetIndex(NamespaceUri);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"the server does not publish namespace '{NamespaceUri}'; refusing to guess an index.");
        }

        _namespaceIndex = (ushort)index;
    }

    /// <summary>FR-004 / R-01: recovery without a process restart.</summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        ReconnectCount++;
    }

    public async Task<OpcUaFrame> ReadAsync(string equipmentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentId);
        var session = _session ?? throw new InvalidOperationException("not connected");

        var nodeIds = new List<NodeId>();
        foreach (var identifier in MeasurementIdentifiers)
        {
            nodeIds.Add(NodeOf(equipmentId, identifier));
        }

        nodeIds.Add(NodeOf(equipmentId, "State"));
        nodeIds.Add(NodeOf(equipmentId, "SequenceNo"));
        nodeIds.Add(NodeOf(equipmentId, "SourceEpochMs"));

        // One batch read, not eleven round trips: the whole point of the block read on the Modbus
        // side applies here too - a sample assembled from separate reads can straddle two model
        // steps and be physically inconsistent.
        var (values, _) = await session.ReadValuesAsync(nodeIds, cancellationToken);

        var readings = new Dictionary<Channel, double?>();
        var flags = new List<ChannelFlag>();
        var sourceTime = DateTimeOffset.MinValue;

        for (var i = 0; i < Measurements.Length; i++)
        {
            var value = values[i];
            readings[Measurements[i]] = Translate(Measurements[i], value, flags);

            if (value.SourceTimestamp > sourceTime.UtcDateTime)
            {
                sourceTime = new DateTimeOffset(value.SourceTimestamp, TimeSpan.Zero);
            }
        }

        return new OpcUaFrame
        {
            Values = readings,
            StatusFlags = flags,
            State = StateFrom(Convert.ToUInt16(values[^3].Value ?? (ushort)0)),
            Sequence = Convert.ToUInt64(values[^2].Value ?? 0UL),
            SourceEpochMs = Convert.ToUInt32(values[^1].Value ?? 0u),
            SourceTimeUtc = sourceTime == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : sourceTime,
        };
    }

    /// <summary>
    /// §1.4 status translation. A <c>Bad_*</c> status yields <c>null</c> — the value is discarded,
    /// not carried with a warning, because a bad reading that reaches the gates is worse than none.
    /// </summary>
    private static double? Translate(Channel channel, DataValue value, List<ChannelFlag> flags)
    {
        var status = value.StatusCode;

        if (StatusCode.IsBad(status))
        {
            flags.Add(new ChannelFlag(channel, status.Code switch
            {
                StatusCodes.BadNoCommunication => QualityFlag.SensorDisconnected,
                _ => QualityFlag.SensorMissing, // Bad_OutOfService, Bad_SensorFailure, and the rest
            }));

            return null;
        }

        if (StatusCode.IsUncertain(status))
        {
            flags.Add(new ChannelFlag(channel, QualityFlag.StaleReading));
        }
        else if (status.Code == StatusCodes.GoodLocalOverride)
        {
            flags.Add(new ChannelFlag(channel, QualityFlag.OutlierSuspected));
        }

        return value.Value is null ? null : Convert.ToDouble(value.Value);
    }

    private NodeId NodeOf(string equipmentId, string identifier)
        => new($"Eq.{equipmentId}.{identifier}", _namespaceIndex);

    private static EquipmentState StateFrom(ushort code) => code switch
    {
        0 => EquipmentState.Offline,
        1 => EquipmentState.Connecting,
        2 => EquipmentState.Idle,
        3 => EquipmentState.Running,
        4 => EquipmentState.Degraded,
        5 => EquipmentState.Fault,
        6 => EquipmentState.Stopping,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown equipment state code (§3)"),
    };

    private static async Task<EndpointDescription> SelectEndpointAsync(
        ApplicationConfiguration configuration, string url, CancellationToken cancellationToken)
    {
        using var discovery = await DiscoveryClient.CreateAsync(
            configuration, new Uri(url), DiagnosticsMasks.None, cancellationToken);

        var endpoints = await discovery.GetEndpointsAsync(null, cancellationToken);

        // Demo profile: SecurityPolicy None (§1.1). Production-like is Basic256Sha256 with
        // SignAndEncrypt, and selecting it is a SECURITY_BOUNDARIES.md decision.
        return endpoints.FirstOrDefault(e => e.SecurityMode == MessageSecurityMode.None)
               ?? throw new InvalidOperationException($"no endpoint at {url} offers the expected security policy.");
    }

    private static ApplicationConfiguration BuildConfiguration(string? pkiRoot)
    {
        var pki = pkiRoot ?? Path.Combine(Path.GetTempPath(), "mair-opcua-gateway-pki");

        return new ApplicationConfiguration
        {
            ApplicationName = "mair-edge-gateway",
            ApplicationUri = "urn:localhost:mair:edge-gateway",
            ProductUri = "https://mair.local/edge-gateway",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = "CN=mair-edge-gateway, O=MAIR",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "trusted"),
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "issuer"),
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "rejected"),
                },
                AutoAcceptUntrustedCertificates = true, // demo profile only
                AddAppCertToTrustedStore = true,
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
        };
    }

    private async Task CloseAsync()
    {
        if (_session is not null)
        {
            await _session.CloseAsync(CancellationToken.None);
            _session.Dispose();
            _session = null;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
