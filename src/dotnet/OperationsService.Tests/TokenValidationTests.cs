using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mair.OperationsService.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Mair.OperationsService.Tests;

/// <summary>
/// ADR-0024 B2: the configured validator, on a real Kestrel host, against tokens from the
/// repository's own minting script and against every refusal the policy names.
/// <para>
/// The accepted token comes from <c>scripts/mint-token.mjs</c>, so the test also proves the two
/// implementations agree on ES256's signature encoding (IEEE P1363) and on the <c>kid</c>.
/// The refused ones are signed here with the same private key, so the only thing wrong with each is
/// the one thing the case names.
/// </para>
/// </summary>
public sealed class TokenValidationTests : IAsyncLifetime
{
    private static readonly string Root = FindRoot();
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "mair-keys-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _http;
    private JsonObject _privateJwk = [];

    private string PublicKey => Path.Combine(_keys, "operations-api-es256.public.jwk");

    public async Task InitializeAsync()
    {
        // The first run of the script generates the key pair into the directory it is given.
        Mint("alice", "viewer");
        _privateJwk = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_keys, "operations-api-es256.private.jwk")))!.AsObject();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOperationsAuth(new TokenSettings(TokenSettings.LocalIssuer, TokenSettings.ApiAudience, [PublicKey]), TimeProvider.System);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/read", (System.Security.Claims.ClaimsPrincipal user) =>
            new { sub = user.FindFirst("sub")?.Value, roles = user.FindAll(OperationsAuth.RoleClaim).Select(c => c.Value).ToArray() })
            .RequireAuthorization(OperationsAuth.ReadPolicy);
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        Directory.Delete(_keys, recursive: true);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "mint-token.mjs")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("could not find the repository root");
    }

    private string Mint(string sub, string roles, int lifetime = 900)
    {
        var psi = new ProcessStartInfo("node", [Path.Combine(Root, "scripts", "mint-token.mjs"), "--sub", sub, "--roles", roles, "--lifetime", lifetime.ToString(System.Globalization.CultureInfo.InvariantCulture), "--keys-dir", _keys])
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

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private JsonObject Claims(Action<JsonObject>? change = null)
    {
        var claims = new JsonObject
        {
            ["iss"] = TokenSettings.LocalIssuer,
            ["aud"] = TokenSettings.ApiAudience,
            ["sub"] = "bob",
            ["roles"] = new JsonArray("viewer"),
            ["iat"] = Now,
            ["exp"] = Now + 600,
        };
        change?.Invoke(claims);
        return claims;
    }

    private static string B64(byte[] bytes) => Base64UrlEncoder.Encode(bytes);

    private static string B64(JsonNode node) => B64(Encoding.UTF8.GetBytes(node.ToJsonString()));

    /// <summary>ES256 with the API's own key pair, unless the case says otherwise.</summary>
    private string Sign(JsonObject claims, string? kid = "same", ECDsa? key = null)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "JWT" };
        if (kid is not null)
        {
            header["kid"] = kid == "same" ? (string)_privateJwk["kid"]! : kid;
        }

        var input = $"{B64(header)}.{B64(claims)}";
        using var own = key is null ? OwnKey() : null;
        var signature = (key ?? own!).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{input}.{B64(signature)}";
    }

    private ECDsa OwnKey() => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D = Base64UrlEncoder.DecodeBytes((string)_privateJwk["d"]!),
        Q = new ECPoint
        {
            X = Base64UrlEncoder.DecodeBytes((string)_privateJwk["x"]!),
            Y = Base64UrlEncoder.DecodeBytes((string)_privateJwk["y"]!),
        },
    });

    private async Task<HttpResponseMessage> ReadAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/read");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _http!.SendAsync(request);
    }

    [Fact]
    public async Task AScriptMintedTokenIsAccepted_AndSubAndRolesArriveUnrenamed()
    {
        using var response = await ReadAsync(Mint("alice", "viewer,operator"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("alice", body.RootElement.GetProperty("sub"u8).GetString());
        Assert.Equal(["viewer", "operator"], body.RootElement.GetProperty("roles"u8).EnumerateArray().Select(r => r.GetString()));
    }

    public static TheoryData<string> Refused =>
    [
        "no token", "alg none", "HS256 with the public key's bytes", "RS256", "wrong aud", "wrong iss",
        "expired beyond the skew", "nbf in the future beyond the skew", "no exp", "no iat",
        "lifetime over an hour", "iat in the future beyond the skew", "no sub", "no kid",
        "unknown kid", "signed by another key",
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task EachTokenThePolicyNamesIsRefused(string defect)
    {
        var token = defect switch
        {
            "no token" => null,
            "alg none" => $"{B64(new JsonObject { ["alg"] = "none", ["typ"] = "JWT", ["kid"] = (string)_privateJwk["kid"]! })}.{B64(Claims())}.",
            "HS256 with the public key's bytes" => Hs256(Claims()),
            "RS256" => Rs256(Claims()),
            "wrong aud" => Sign(Claims(c => c["aud"] = "someone-else")),
            "wrong iss" => Sign(Claims(c => c["iss"] = "someone-else")),
            "expired beyond the skew" => Sign(Claims(c => { c["iat"] = Now - 900; c["exp"] = Now - 61; })),
            "nbf in the future beyond the skew" => Sign(Claims(c => c["nbf"] = Now + 120)),
            "no exp" => Sign(Claims(c => c.Remove("exp"))),
            "no iat" => Sign(Claims(c => c.Remove("iat"))),
            "lifetime over an hour" => Sign(Claims(c => c["exp"] = Now + 3601)),
            "iat in the future beyond the skew" => Sign(Claims(c => { c["iat"] = Now + 1800; c["exp"] = Now + 3000; })),
            "no sub" => Sign(Claims(c => c.Remove("sub"))),
            "no kid" => Sign(Claims(), kid: null),
            "unknown kid" => Sign(Claims(), kid: "not-a-configured-key"),
            "signed by another key" => Sign(Claims(), key: ECDsa.Create(ECCurve.NamedCurves.nistP256)),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        using var response = await ReadAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> NoRecognisedRole => ["roles missing", "roles not an array", "only unknown roles"];

    [Theory]
    [MemberData(nameof(NoRecognisedRole))]
    public async Task AValidTokenWithNoRecognisedRoleIsForbidden_NotUnauthorised(string defect)
    {
        var token = Sign(Claims(c =>
        {
            switch (defect)
            {
                case "roles missing": c.Remove("roles"); break;
                case "roles not an array": c["roles"] = "operator"; break;
                default: c["roles"] = new JsonArray("admin", "root"); break;
            }
        }));

        using var response = await ReadAsync(token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARoleClaimUnderTheInternalNameIsNotTrusted()
    {
        // The code checks its own claim type; a token asserting it directly must gain nothing.
        using var response = await ReadAsync(Sign(Claims(c => { c.Remove("roles"); c[OperationsAuth.RoleClaim] = "operator"; })));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void NoAuthorityOrMetadataAddressIsConfigured_SoNothingIsFetched()
    {
        var options = _app!.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Null(options.Authority);
        Assert.True(string.IsNullOrEmpty(options.MetadataAddress));
        Assert.Null(options.ConfigurationManager);
        Assert.False(options.MapInboundClaims);
    }

    [Fact]
    public void AKeySetWithADuplicateKid_OrAPrivateKey_FailsStartup()
    {
        var copy = Path.Combine(_keys, "copy.public.jwk");
        File.Copy(PublicKey, copy);
        Assert.Throws<KeySetException>(() => OperationsAuth.LoadKeys([PublicKey, copy]));

        Assert.Throws<KeySetException>(() => OperationsAuth.LoadKeys([Path.Combine(_keys, "operations-api-es256.private.jwk")]));
    }

    private string Hs256(JsonObject claims)
    {
        // The classic algorithm-confusion attack: an HMAC keyed with the public key's own text.
        var input = $"{B64(new JsonObject { ["alg"] = "HS256", ["typ"] = "JWT", ["kid"] = (string)_privateJwk["kid"]! })}.{B64(claims)}";
        var mac = HMACSHA256.HashData(File.ReadAllBytes(PublicKey), Encoding.ASCII.GetBytes(input));
        return $"{input}.{B64(mac)}";
    }

    private string Rs256(JsonObject claims)
    {
        using var rsa = RSA.Create(2048);
        var input = $"{B64(new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = (string)_privateJwk["kid"]! })}.{B64(claims)}";
        return $"{input}.{B64(rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
    }
}
