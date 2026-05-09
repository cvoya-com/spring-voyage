// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Mints per-invocation callback JWTs scoped to one tenant, one agent
/// address, one thread, and one inbound message.
/// </summary>
/// <remarks>
/// Issuance is symmetric — Spring Voyage signs and validates its own
/// callback tokens. The signing key is resolved per tenant through
/// <see cref="ITenantSigningKeyProvider"/> so hosted deployments can swap
/// in a KMS-backed implementation without changing launcher code.
/// </remarks>
/// <param name="signingKeyProvider">Per-tenant signing-key source.</param>
/// <param name="options">Callback-token options (lifetime, issuer, audience).</param>
/// <param name="timeProvider">
/// Clock used for <c>iat</c> / <c>nbf</c> / <c>exp</c>. Defaults to
/// <see cref="TimeProvider.System"/>.
/// </param>
public class CallbackTokenIssuer(
    ITenantSigningKeyProvider signingKeyProvider,
    IOptions<CallbackTokenOptions> options,
    TimeProvider? timeProvider = null) : ICallbackTokenIssuer
{
    private readonly ITenantSigningKeyProvider _signingKeyProvider = signingKeyProvider;
    private readonly CallbackTokenOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public virtual string Issue(CallbackToken claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var now = _timeProvider.GetUtcNow();
        var expires = claims.ExpiresAt > now
            ? claims.ExpiresAt
            : now.Add(_options.Lifetime);

        var keyBytes = _signingKeyProvider.GetSigningKey(claims.TenantId);
        var signingKey = new SymmetricSecurityKey(keyBytes);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(CallbackTokenClaimNames.TenantId, GuidFormatter.Format(claims.TenantId)),
            new Claim(CallbackTokenClaimNames.AgentAddress, claims.AgentAddress.ToString()),
            new Claim(CallbackTokenClaimNames.ThreadId, GuidFormatter.Format(claims.ThreadId)),
            new Claim(CallbackTokenClaimNames.MessageId, GuidFormatter.Format(claims.MessageId)),
        });

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = CallbackTokenOptions.Issuer,
            Audience = CallbackTokenOptions.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = identity,
            SigningCredentials = credentials,
        };

        // Stamp `exp` as a deterministic Unix-seconds claim so the wire
        // representation does not drift across BCL changes — the validator
        // round-trips on epoch seconds via JwtSecurityToken.ValidTo.
        _ = expires.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(descriptor);
        return handler.WriteToken(token);
    }
}
