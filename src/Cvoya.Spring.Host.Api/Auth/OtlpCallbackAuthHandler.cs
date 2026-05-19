// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Runtime;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Authentication handler for the OTLP ingest endpoints (issue #2492).
/// Validates the per-invocation callback JWT the launcher injects as
/// <c>SPRING_MCP_TOKEN</c> against the per-tenant signing key, then
/// surfaces the token's tenant id + subject address as principal claims.
/// </summary>
/// <remarks>
/// <para>
/// Reusing the launcher's existing per-invocation callback token avoids
/// introducing a new credential primitive — the runtime already speaks
/// this token shape to reach the MCP endpoint, so OTLP ingest sits on
/// the same auth surface. The token is short-lived (<see cref="CallbackTokenOptions.Lifetime"/>),
/// pinned to one tenant, one subject, one thread, and one message; the
/// ingest controller cross-checks the OTel <c>sv.tenant.id</c> /
/// <c>sv.subject.uuid</c> resource attributes against the principal's
/// claims so a leaked token can't be replayed against a different
/// subject.
/// </para>
/// </remarks>
public class OtlpCallbackAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ITenantSigningKeyProvider signingKeyProvider,
    IOptions<CallbackTokenOptions> tokenOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private readonly ITenantSigningKeyProvider _signingKeyProvider = signingKeyProvider;
    private readonly CallbackTokenOptions _tokenOptions = tokenOptions.Value;

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(token))
        {
            // Not a JWT — let other handlers try.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        JwtSecurityToken unverified;
        try
        {
            unverified = handler.ReadJwtToken(token);
        }
        catch (Exception)
        {
            return Task.FromResult(AuthenticateResult.Fail("OTLP callback token could not be parsed."));
        }

        var tenantClaim = unverified.Claims.FirstOrDefault(c => c.Type == CallbackTokenClaimNames.TenantId)?.Value;
        if (string.IsNullOrEmpty(tenantClaim) || !GuidFormatter.TryParse(tenantClaim, out var tenantId))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"OTLP callback token is missing or has an invalid '{CallbackTokenClaimNames.TenantId}' claim."));
        }

        byte[] keyBytes;
        try
        {
            keyBytes = _signingKeyProvider.GetSigningKey(tenantId);
        }
        catch (Exception)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"No signing key configured for tenant '{GuidFormatter.Format(tenantId)}'."));
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = CallbackTokenOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = CallbackTokenOptions.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ClockSkew = _tokenOptions.ClockSkew,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        };

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(token, parameters, out _);
        }
        catch (SecurityTokenExpiredException)
        {
            return Task.FromResult(AuthenticateResult.Fail("OTLP callback token has expired."));
        }
        catch (SecurityTokenException)
        {
            return Task.FromResult(AuthenticateResult.Fail("OTLP callback token failed validation."));
        }

        // Re-stamp the validated principal onto a clean identity tied to
        // this auth scheme so downstream policies see consistent claims.
        var identity = new ClaimsIdentity(AuthConstants.OtlpCallbackScheme);
        foreach (var claim in principal.Claims)
        {
            identity.AddClaim(claim);
        }

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            AuthConstants.OtlpCallbackScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
