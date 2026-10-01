using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mair.OperationsService.Auth;

/// <summary>What the API trusts: one issuer, one audience, and public keys only (<c>OD-017</c>).</summary>
public sealed record TokenSettings(string Issuer, string Audience, IReadOnlyList<string> PublicKeyFiles)
{
    public const string LocalIssuer = "mair-local-issuer";
    public const string ApiAudience = "mair-operations-api";
}

/// <summary>The key set is unusable, and the service does not start with it.</summary>
public sealed class KeySetException(string message) : Exception(message);

/// <summary>
/// Bearer-token validation for the Operations API — the policy table in <c>ADR-0024</c>, applied
/// with <c>JwtBearer</c> and nothing hand-rolled where the platform has a rule.
/// <para>
/// <b>No network.</b> No authority and no metadata address are configured, so <c>JwtBearer</c> never
/// builds a configuration manager: the keys are the files it was given, and nothing else.
/// </para>
/// </summary>
public static class OperationsAuth
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string ReadPolicy = "read";
    public const string OperatePolicy = "operate";

    /// <summary>
    /// The role claim type the code checks. Not <c>roles</c>: that claim is read from the token,
    /// checked to be an array of strings, and only recognised values are copied here, so a token
    /// cannot assert a role by any other shape.
    /// </summary>
    internal const string RoleClaim = "mair:role";

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(3600);

    public static AuthenticationBuilder AddOperationsAuth(this IServiceCollection services, TokenSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        var keys = LoadKeys(settings.PublicKeyFiles);

        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, p => p.RequireAuthenticatedUser().RequireRole(Viewer, Operator))
            .AddPolicy(OperatePolicy, p => p.RequireAuthenticatedUser().RequireRole(Operator));

        return services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            // Claim names reach the code as issued: no renaming of sub or roles.
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = settings.Issuer,
                ValidAudience = settings.Audience,
                IssuerSigningKeys = keys,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                // kid is required: with this off, a token without one is not tried against every key.
                TryAllIssuerSigningKeys = false,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = ClockSkew,
                LifetimeValidator = (notBefore, expires, token, _) => LifetimeIsValid(notBefore, expires, token, time),
                NameClaimType = "sub",
                RoleClaimType = RoleClaim,
            };
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = Admit,

                // AC-030: an error is problem+json like any other, not an empty 401/403.
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return Api.Problems.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized",
                        "a valid bearer token is required");
                },
                OnForbidden = context => Api.Problems.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Forbidden",
                    "the token carries no role this route allows"),
            };
        });
    }

    /// <summary>
    /// <c>exp</c> and <c>iat</c> required; lifetime at most an hour; <c>iat</c> and <c>nbf</c> not in
    /// the future beyond the skew. Replaces the default validator, so expiry is checked here too.
    /// </summary>
    internal static bool LifetimeIsValid(DateTime? notBefore, DateTime? expires, SecurityToken token, TimeProvider time)
    {
        if (expires is null || token is not JsonWebToken jwt || !jwt.TryGetPayloadValue<long>("iat", out var iatSeconds))
        {
            return false;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds).UtcDateTime;

        return expires.Value.ToUniversalTime() + ClockSkew >= now
            && (notBefore is null || notBefore.Value.ToUniversalTime() - ClockSkew <= now)
            // Without this an iat far in the future passes the lifetime check below (ADR-0024 C08).
            && issuedAt - ClockSkew <= now
            && expires.Value.ToUniversalTime() - issuedAt <= MaxLifetime;
    }

    /// <summary>After the signature and lifetime: <c>sub</c> required, roles copied only from an array.</summary>
    private static Task Admit(TokenValidatedContext context)
    {
        if (context.SecurityToken is not JsonWebToken jwt || context.Principal?.Identity is not ClaimsIdentity identity)
        {
            context.Fail("unexpected token type");
            return Task.CompletedTask;
        }

        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
        var root = payload.RootElement;

        if (!root.TryGetProperty("sub"u8, out var sub) || sub.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sub.GetString()))
        {
            // Rate limits partition on sub (OD-018); a token without one has no identity to limit.
            context.Fail("token has no sub");
            return Task.CompletedTask;
        }

        foreach (var claim in identity.FindAll(RoleClaim).ToList())
        {
            identity.RemoveClaim(claim);
        }

        if (root.TryGetProperty("roles"u8, out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.String && role.GetString() is Viewer or Operator)
                {
                    identity.AddClaim(new Claim(RoleClaim, role.GetString()!));
                }
            }
        }

        // A valid token with no recognised role stays authenticated and is refused by policy: 403, not 401.
        return Task.CompletedTask;
    }

    /// <summary>Public EC P-256 keys with distinct <c>kid</c>s. A private key here is refused.</summary>
    public static IReadOnlyList<SecurityKey> LoadKeys(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new KeySetException("no public key configured");
        }

        var keys = new List<SecurityKey>();
        foreach (var file in files)
        {
            var jwk = new JsonWebKey(File.ReadAllText(file, Encoding.UTF8));
            if (jwk.Kty != "EC" || jwk.Crv != "P-256" || string.IsNullOrEmpty(jwk.X) || string.IsNullOrEmpty(jwk.Y))
            {
                throw new KeySetException($"{file} is not an EC P-256 public key");
            }

            if (!string.IsNullOrEmpty(jwk.D))
            {
                // The verifier must not be able to mint (OD-017).
                throw new KeySetException($"{file} holds a private key; the API is given public keys only");
            }

            if (string.IsNullOrEmpty(jwk.Kid))
            {
                throw new KeySetException($"{file} has no kid");
            }

            if (keys.Any(k => k.KeyId == jwk.Kid))
            {
                throw new KeySetException($"kid {jwk.Kid} appears twice in the key set");
            }

            keys.Add(jwk);
        }

        return keys;
    }
}
