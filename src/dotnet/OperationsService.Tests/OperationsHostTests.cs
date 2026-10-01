using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Mair.OperationsService.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// The process as <c>Program</c> starts it (<c>OPERATIONS_API.md</c> §15): configuration, migration
/// before listening, projector, readiness gate.
/// </summary>
public sealed class OperationsHostTests(ProjectionRig rig) : IClassFixture<ProjectionRig>, IDisposable
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "mair-host-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_keys))
        {
            Directory.Delete(_keys, recursive: true);
        }
    }

    private string Mint(string sub)
    {
        var psi = new ProcessStartInfo("node", [Path.Combine(ProjectionRig.Root, "scripts", "mint-token.mjs"), "--sub", sub, "--roles", "viewer", "--keys-dir", _keys])
        {
            RedirectStandardOutput = true,
        };
        using var node = Process.Start(psi)!;
        var token = node.StandardOutput.ReadToEnd().Trim();
        node.WaitForExit();
        return token;
    }

    private Dictionary<string, string?> Settings(string database) => new()
    {
        ["ConnectionStrings:Operations"] = rig.Postgres.ConnectionString(database),
        ["Mair:Kafka:BootstrapServers"] = rig.Kafka.BootstrapServers,
        ["Mair:Contracts"] = ProjectionRig.Contracts,
        ["Mair:Tokens:PublicKeyFiles:0"] = Path.Combine(_keys, "operations-api-es256.public.jwk"),
        ["urls"] = "http://127.0.0.1:0",
    };

    private static Task<WebApplication> BuildAsync(Dictionary<string, string?> settings) =>
        OperationsHost.BuildAsync([], b => b.Configuration.AddInMemoryCollection(settings));

    [Theory]
    [InlineData("ConnectionStrings:Operations")]
    [InlineData("Mair:Kafka:BootstrapServers")]
    [InlineData("Mair:Contracts")]
    [InlineData("Mair:Tokens:PublicKeyFiles:0")]
    public async Task AMissingRequiredKeyStopsStartup_NamingTheKey(string key)
    {
        Mint("setup");
        var settings = Settings(await rig.Postgres.CreateDatabaseAsync());
        settings.Remove(key);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(settings));
        Assert.Contains(key.Replace(":0", "", StringComparison.Ordinal), ex.Message);
    }

    [Fact]
    public async Task AMigrationThatDoesNotMatchStopsStartupBeforeAnythingListens()
    {
        Mint("setup");
        var database = await rig.Postgres.CreateDatabaseAsync();
        await using (var db = rig.Postgres.DataSource(database))
        {
            // Version 1 recorded with a checksum no file has: the runner refuses, the host never builds.
            await new MigrationRunner(db, "operations", 1).RunAsync([new Migration(1, "projection_tables", "SELECT 1;")]);
        }

        await Assert.ThrowsAsync<MigrationException>(() => BuildAsync(Settings(database)));
    }

    [Fact]
    public async Task TheHostMigrates_ProjectsAndAnswers503UntilCaughtUp_Then200()
    {
        var token = Mint("host");
        var database = await rig.Postgres.CreateDatabaseAsync();
        var app = await BuildAsync(Settings(database));

        await using (var db = rig.Postgres.DataSource(database))
        await using (var cmd = db.CreateCommand("SELECT count(*) FROM operations.schema_migrations"))
        {
            // Migrated by BuildAsync, before the host started.
            Assert.Equal((long)MigrationRunner.Embedded("operations").Count, await cmd.ExecuteScalarAsync());
        }

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var deadline = DateTime.UtcNow.AddSeconds(90);
            var sawGate = false;
            HttpStatusCode status;
            do
            {
                using var response = await http.GetAsync("/api/v1/equipment");
                status = response.StatusCode;
                sawGate |= status == HttpStatusCode.ServiceUnavailable;
                Assert.True(DateTime.UtcNow < deadline, "the host never became ready");
                await Task.Delay(200);
            }
            while (status != HttpStatusCode.OK);

            Assert.True(sawGate, "the first request was served before any projector had caught up");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
