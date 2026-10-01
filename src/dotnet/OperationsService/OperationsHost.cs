using Confluent.Kafka;
using Mair.OperationsService.Api;
using Mair.OperationsService.Auth;
using Mair.OperationsService.Database;
using Mair.OperationsService.Projection;
using Npgsql;

namespace Mair.OperationsService;

/// <summary>
/// The operations-service process, assembled as <c>OPERATIONS_API.md</c> §15 orders it: migrate the
/// <c>operations</c> schema, start the projector, then serve. <c>Program</c> calls this; the tests call
/// it too, so what they start is what runs.
/// </summary>
public static class OperationsHost
{
    /// <summary>
    /// Builds the host and migrates. A migration failure throws here, before anything listens
    /// (<c>OPERATIONAL_DATA.md</c> §11).
    /// </summary>
    public static async Task<WebApplication> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        configure?.Invoke(builder);
        var config = builder.Configuration;

        string Required(string key) =>
            config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"configuration key '{key}' is required (OPERATIONS_API.md §15)");

        var connection = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Operations")
            ?? throw new InvalidOperationException("configuration key 'ConnectionStrings:Operations' is required (OPERATIONS_API.md §15)"))
        {
            // §12: 3 s, whatever the connection string says.
            CommandTimeout = 3,
        };
        var bootstrapServers = Required("Mair:Kafka:BootstrapServers");
        var contracts = Required("Mair:Contracts");
        var keyFiles = config.GetSection("Mair:Tokens:PublicKeyFiles").GetChildren().Select(c => c.Value!).Where(v => v.Length > 0).ToList();
        if (keyFiles.Count == 0)
        {
            throw new InvalidOperationException("configuration key 'Mair:Tokens:PublicKeyFiles' is required (OPERATIONS_API.md §15)");
        }

        var db = NpgsqlDataSource.Create(connection.ConnectionString);
        await new MigrationRunner(db, "operations", 1).RunAsync(MigrationRunner.Embedded("operations"));

        var deadLetters = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            AllowAutoCreateTopics = false,
        }).Build();
        var projector = new OperationsProjector(bootstrapServers, db, contracts, deadLetters);
        var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(projector);
        builder.Services.AddSingleton(deadLetters);
        builder.Services.AddSingleton(admin);
        builder.Services.AddHostedService<ProjectorService>();
        builder.Services.AddOperationsAuth(
            new TokenSettings(config["Mair:Tokens:Issuer"] ?? TokenSettings.LocalIssuer, TokenSettings.ApiAudience, keyFiles),
            TimeProvider.System);
        builder.Services.AddOperationsApi();

        var app = builder.Build();
        app.MapOperationsApi(db, new ProjectorStatus(projector), new KafkaProbe(admin, TimeProvider.System), TimeProvider.System);
        return app;
    }

    /// <summary>The projector's loops, for the life of the host; disposed with it.</summary>
    private sealed class ProjectorService(OperationsProjector projector) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await projector.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Stopping mid-backoff: the record was not committed and is redelivered on restart.
            }
        }

        public override void Dispose()
        {
            projector.Dispose();
            base.Dispose();
        }
    }
}
