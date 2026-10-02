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

        var runtime = config.GetConnectionString("Operations")
            ?? throw new InvalidOperationException("configuration key 'ConnectionStrings:Operations' is required (OPERATIONS_API.md §15)");
        var profile = config["Mair:Profile"] ?? "local";
        var owner = config.GetConnectionString("OperationsMigrations")
            ?? (profile == "production-like"
                ? throw new InvalidOperationException("configuration key 'ConnectionStrings:OperationsMigrations' is required in production-like (OPERATIONS_API.md §15)")
                : runtime);
        var bootstrapServers = Required("Mair:Kafka:BootstrapServers");
        var contracts = Required("Mair:Contracts");
        var keyFiles = config.GetSection("Mair:Tokens:PublicKeyFiles").GetChildren().Select(c => c.Value!).Where(v => v.Length > 0).ToList();
        if (keyFiles.Count == 0)
        {
            throw new InvalidOperationException("configuration key 'Mair:Tokens:PublicKeyFiles' is required (OPERATIONS_API.md §15)");
        }

        await using (var migrations = NpgsqlDataSource.Create(owner))
        {
            await new MigrationRunner(migrations, "operations", 1).RunAsync(MigrationRunner.Embedded("operations"));
        }

        // One data source per role (OD-023): the API's sessions can only read, the projector's cannot
        // change an audit row, the retention job's can call one function.
        var reader = OperationsRoles.DataSource(runtime, OperationsRoles.Reader);
        var writer = OperationsRoles.DataSource(runtime, OperationsRoles.Projector);
        var retention = OperationsRoles.DataSource(runtime, OperationsRoles.Retention);

        var deadLetters = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            AllowAutoCreateTopics = false,
        }).Build();
        var projector = new OperationsProjector(bootstrapServers, writer, contracts, deadLetters);
        var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        builder.Services.AddSingleton(projector);
        builder.Services.AddSingleton(new RetentionJob(retention, TimeProvider.System));
        builder.Services.AddHostedService<RetentionService>();
        builder.Services.AddSingleton(deadLetters);
        builder.Services.AddSingleton(admin);
        builder.Services.AddHostedService<ProjectorService>();
        builder.Services.AddOperationsAuth(
            new TokenSettings(config["Mair:Tokens:Issuer"] ?? TokenSettings.LocalIssuer, TokenSettings.ApiAudience, keyFiles),
            TimeProvider.System);
        builder.Services.AddOperationsApi();

        var app = builder.Build();
        app.MapOperationsApi(reader, new ProjectorStatus(projector), new KafkaProbe(admin, TimeProvider.System), TimeProvider.System);
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
