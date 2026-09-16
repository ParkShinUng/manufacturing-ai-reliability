using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace Mair.EquipmentSimulator.Protocols;

/// <summary>
/// The simulator's OPC UA server. Endpoint <c>opc.tcp://{host}:4840/mair/</c>, namespace
/// <c>urn:mair:equipment:v1</c> (`OT_PROTOCOL_MAPPING.md` §1.1).
/// </summary>
public sealed class OpcUaServerHost : IAsyncDisposable
{
    public const int DefaultPort = 4840;
    public const string EndpointPath = "/mair/";

    private readonly ApplicationInstance _application;
    private readonly MairServer _server;

    private OpcUaServerHost(ApplicationInstance application, MairServer server, string endpointUrl)
    {
        _application = application;
        _server = server;
        EndpointUrl = endpointUrl;
    }

    public string EndpointUrl { get; }

    public MairNodeManager NodeManager => _server.NodeManager
        ?? throw new InvalidOperationException("the node manager is created when the server starts");

    /// <param name="securityPolicy">
    /// <c>None</c> is the documented **demo** policy. Production-like is `Basic256Sha256` with
    /// `SignAndEncrypt`; choosing it is a `SECURITY_BOUNDARIES.md` decision, not this host's.
    /// </param>
    public static async Task<OpcUaServerHost> StartAsync(
        IEquipmentAccess equipment,
        IReadOnlyList<string> equipmentIds,
        int port = DefaultPort,
        string? pkiRoot = null,
        string securityPolicy = SecurityPolicies.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(equipmentIds);

        var endpointUrl = $"opc.tcp://localhost:{port}{EndpointPath}";
        var pki = pkiRoot ?? Path.Combine(Path.GetTempPath(), "mair-opcua-pki");

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "mair-equipment-simulator",
            ApplicationUri = "urn:localhost:mair:equipment-simulator",
            ProductUri = "https://mair.local/equipment-simulator",
            ApplicationType = ApplicationType.Server,

            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { endpointUrl },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = securityPolicy == SecurityPolicies.None
                            ? MessageSecurityMode.None
                            : MessageSecurityMode.SignAndEncrypt,
                        SecurityPolicyUri = securityPolicy,
                    },
                },
                MaxSessionCount = 100,

                // §1.3: publishing 100 ms to match the telemetry cadence, sampling 50 ms so the
                // sampler is Nyquist-safe against it.
                MinPublishingInterval = 100,
                MaxPublishingInterval = 3_600_000,
                MinSubscriptionLifetime = 10_000,
                AvailableSamplingRates = { new SamplingRateGroup(50, 50, 20) },
            },

            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = "CN=mair-equipment-simulator, O=MAIR",
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

                // Demo profile only. Under the production-like policy the gateway and the Control
                // Service present distinct certificates and this must be false, or the OPC UA half
                // of FR-035 would be decoration.
                AutoAcceptUntrustedCertificates = securityPolicy == SecurityPolicies.None,
                AddAppCertToTrustedStore = true,
            },

            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration(),
        };

        await configuration.ValidateAsync(ApplicationType.Server, cancellationToken);

        // The stack's diagnostics go through ILogger now; TraceConfiguration and the
        // telemetry-less ApplicationInstance constructor are both deprecated in 1.5.378.
        var telemetry = DefaultTelemetry.Create(_ => { });

        var application = new ApplicationInstance(configuration, telemetry)
        {
            ApplicationName = configuration.ApplicationName,
            ApplicationType = ApplicationType.Server,
        };

        await application.CheckApplicationInstanceCertificatesAsync(
            silent: true, lifeTimeInMonths: null, ct: cancellationToken);

        var server = new MairServer(equipment, equipmentIds);
        await application.StartAsync(server);

        return new OpcUaServerHost(application, server, endpointUrl);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        _server.Dispose();
    }

    private sealed class MairServer(IEquipmentAccess equipment, IReadOnlyList<string> equipmentIds) : StandardServer
    {
        public MairNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new MairNodeManager(server, configuration, equipment, equipmentIds);
            return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, [NodeManager]);
        }
    }
}
