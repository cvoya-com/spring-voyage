// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Auth;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Mints per-invocation callback JWTs scoped to one tenant, one agent
/// address, one thread, and one inbound message. The launcher (ADR-0039
/// D14) calls <see cref="Issue"/> at runtime-container launch time and
/// injects the resulting token as the <c>SPRING_CALLBACK_TOKEN</c> env var.
/// </summary>
/// <remarks>
/// Issuance is symmetric — the dispatcher signs, the dispatcher validates;
/// no second party needs the key. The signing key is resolved per tenant
/// through <see cref="ITenantSigningKeyProvider"/> so the cloud overlay can
/// swap in a KMS-backed implementation without touching the issuer or the
/// validator.
/// </remarks>
/// <param name="signingKeyProvider">Per-tenant signing-key source.</param>
/// <param name="options">Callback-token options (lifetime, issuer, audience).</param>
/// <param name="timeProvider">
/// Clock used for <c>iat</c> / <c>nbf</c> / <c>exp</c>. Defaults to
/// <see cref="TimeProvider.System"/>; tests inject a deterministic provider
/// to drive the expiry path.
/// </param>
public class CallbackTokenIssuer(
    ITenantSigningKeyProvider signingKeyProvider,
    IOptions<CallbackTokenOptions> options,
    TimeProvider? timeProvider = null)
{
    /// <summary>Claim name carrying the tenant id (canonical no-dash hex).</summary>
    public const string TenantIdClaim = "sv_tid";

    /// <summary>Claim name carrying the canonical agent / unit address (e.g. <c>unit:&lt;hex&gt;</c>).</summary>
    public const string AgentAddressClaim = "sv_addr";

    /// <summary>Claim name carrying the thread id (canonical no-dash hex).</summary>
    public const string ThreadIdClaim = "sv_thread";

    /// <summary>Claim name carrying the inbound message id (canonical no-dash hex).</summary>
    public const string MessageIdClaim = "sv_msg";

    private readonly ITenantSigningKeyProvider _signingKeyProvider = signingKeyProvider;
    private readonly CallbackTokenOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Issues a signed JWT carrying the supplied claims. The token's
    /// <c>iat</c> / <c>nbf</c> are stamped from <see cref="TimeProvider"/>;
    /// <c>exp</c> uses <see cref="CallbackToken.ExpiresAt"/> when supplied
    /// (must be in the future relative to the provider's now), or
    /// <c>now + <see cref="CallbackTokenOptions.Lifetime"/></c> when the
    /// supplied <see cref="CallbackToken.ExpiresAt"/> is at or before now
    /// (treats default-initialized values as "use the configured
    /// lifetime").
    /// </summary>
    /// <param name="claims">Claim shape for the token.</param>
    /// <returns>The signed compact-JWT string.</returns>
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
            new Claim(TenantIdClaim, GuidFormatter.Format(claims.TenantId)),
            new Claim(AgentAddressClaim, claims.AgentAddress.ToString()),
            new Claim(ThreadIdClaim, GuidFormatter.Format(claims.ThreadId)),
            new Claim(MessageIdClaim, GuidFormatter.Format(claims.MessageId)),
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