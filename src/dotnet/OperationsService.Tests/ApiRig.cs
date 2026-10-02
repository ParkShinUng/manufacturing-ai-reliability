using System.Diagnostics;
using System.Net.Http.Headers;
using Confluent.Kafka;
using Mair.OperationsService.Api;
using Mair.OperationsService.Auth;
using Mair.OperationsService.Database;
using Mair.OperationsService.Projection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// The Operations API as it runs: the projector on the register's topics into a migrated database,
/// the API on Kestrel in front of it, ES256 tokens from the repository's own minting script — and
/// every response the tests receive passed through the AC-030 harness.
/// </summary>
public sealed class ApiRig : IAsyncLifetime
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "mair-api-keys-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private Task? _projecting;
    private WebApplication? _app;
    private IAdminClient? _admin;

    public ProjectionRig Infra { get; } = new();

    public OpenApiContract Contract { get; } = OpenApiContract.Load(Path.Combine(ProjectionRig.Root, "contracts", "openapi", "operations-api-v1.yaml"));

    public NpgsqlDataSource Db { get; private set; } = null!;

    public OperationsProjector Projector { get; private set; } = null!;

    public IProducer<string, byte[]> Producer { get; private set; } = null!;

    public HttpClient Http { get; private set; } = null!;

    /// <summary>Every harness failure seen by any test, so a suite cannot pass with a nonconforming response.</summary>
    public List<string> Failures { get; } = [];

    public async Task InitializeAsync()
    {
        await Infra.InitializeAsync();
        Db = await Infra.MigratedAsync();
        Producer = Infra.Producer();
        Projector = Infra.Projector(Db, Producer);
        _projecting = Projector.RunAsync(_stop.Token);

        Mint("bootstrap", "viewer"); // generates the key pair
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOperationsAuth(
            new TokenSettings(TokenSettings.LocalIssuer, TokenSettings.ApiAudience, [Path.Combine(_keys, "operations-api-es256.public.jwk")]),
            TimeProvider.System);
        builder.Services.AddOperationsApi();
        _app = builder.Build();
        _admin = Infra.Kafka.Admin();
        _app.MapOperationsApi(Infra.Role(Db, OperationsRoles.Reader), new ProjectorStatus(Projector), new KafkaProbe(_admin, TimeProvider.System), TimeProvider.System);
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _stop.CancelAsync();
        try
        {
            await _projecting!;
        }
        catch (OperationCanceledException)
        {
        }

        Projector.Dispose();
        Producer.Dispose();
        _admin?.Dispose();
        await _app!.DisposeAsync();
        await Db.DisposeAsync();
        await Infra.DisposeAsync();
        Directory.Delete(_keys, recursive: true);
    }

    public string Mint(string sub, string roles)
    {
        var psi = new ProcessStartInfo("node", [Path.Combine(ProjectionRig.Root, "scripts", "mint-token.mjs"), "--sub", sub, "--roles", roles, "--keys-dir", _keys])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var node = Process.Start(psi)!;
        var token = node.StandardOutput.ReadToEnd().Trim();
        node.WaitForExit();
        Assert.True(node.ExitCode == 0, node.StandardError.ReadToEnd());
        return token;
    }

    /// <summary>Waits until every projector consumer has been caught up after now (OD-018).</summary>
    public async Task CaughtUpAsync()
    {
        var since = DateTimeOffset.UtcNow;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!Projector.CaughtUpSince(since))
        {
            Assert.True(DateTime.UtcNow < deadline, "the projector did not catch up");
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Starts PostgreSQL again and empties the test's own pool, whose connections all died with the
    /// server. The projector's and the API's data sources clear theirs on their first failure.
    /// </summary>
    public async Task RestartPostgresAsync()
    {
        await Infra.Postgres.StartAsync();
        Db.Clear();
    }

    /// <summary>
    /// After a store outage: waits out the breaker (OD-022) until a database route answers 200 again,
    /// so the next test does not inherit an open circuit.
    /// </summary>
    public async Task BreakerClosedAsync(string token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while ((await GetAsync("/api/v1/models", token)).Status != 200)
        {
            Assert.True(DateTime.UtcNow < deadline, "the circuit breaker never closed");
            await Task.Delay(1000);
        }
    }

    /// <summary>A GET through the harness. Any mismatch with the contract is recorded and fails the test.</summary>
    public async Task<(int Status, HttpResponseMessage Response, string Body)> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var failures = Contract.Check(new ObservedResponse("GET", path.Replace("/api/v1", "", StringComparison.Ordinal), (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(), headers, body));
        lock (Failures)
        {
            Failures.AddRange(failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        return ((int)response.StatusCode, response, System.Text.Encoding.UTF8.GetString(body));
    }
}
