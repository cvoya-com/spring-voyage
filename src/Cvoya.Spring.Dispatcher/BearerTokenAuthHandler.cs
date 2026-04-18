// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Minimal bearer-token authentication handler backed by
/// <see cref="DispatcherOptions.Tokens"/>. The token itself is opaque — a
/// shared secret issued per worker at deploy time. A successful match yields
/// a <see cref="ClaimsPrincipal"/> carrying the tenant id the token is scoped to.
/// </summary>
/// <remarks>
/// Kept intentionally simple: the OSS standalone deployment does not need JWT
/// verification, key rotation, or revocation lists. The private-cloud repo
/// that targets multi-tenant K8s deployments can replace this handler with a
/// tenant-aware JWT validator by registering its own
/// <see cref="AuthenticationBuilder"/> scheme before calling into the
/// dispatcher host.
/// </remarks>
public class BearerTokenAuthHandler(
    IOptionsMonitor<BearerTokenAuthOptions> options,
    IOptionsMonitor<DispatcherOptions> dispatcherOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<BearerTokenAuthOptions>(options, loggerFactory, encoder)
{
    /// <summary>Authentication scheme name.</summary>
    public const string SchemeName = "DispatcherBearer";

    /// <summary>Claim type that carries the tenant id the token is scoped to.</summary>
    public const string TenantIdClaim = "tenant_id";

    /// <summary>Claim type that carries the opaque token string.</summary>
    public const string TokenClaim = "dispatcher_token";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var headerValue = authHeader.ToString();
        const string bearerPrefix = "Bearer ";
        if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Authorization header is not a bearer token."));
        }

        var token = headerValue[bearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Bearer token is empty."));
        }

        var configured = dispatcherOptions.CurrentValue.Tokens;
        if (!configured.TryGetValue(token, out var scope))
        {
            return Task.FromResult(AuthenticateResult.Fail("Bearer token is not recognised."));
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(TenantIdClaim, scope.TenantId),
                new Claim(TokenClaim, token),
            },
            authenticationType: SchemeName,
            nameType: TokenClaim,
            roleType: ClaimTypes.Role);

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Options for <see cref="BearerTokenAuthHandler"/>. The handler reads tokens
/// from <see cref="DispatcherOptions"/>; this type exists only to satisfy the
/// <see cref="AuthenticationSchemeOptions"/> contract.
/// </summary>
public class BearerTokenAuthOptions : AuthenticationSchemeOptions
{
}