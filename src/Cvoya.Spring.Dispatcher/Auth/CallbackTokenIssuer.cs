// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Auth;

using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;

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
    : Cvoya.Spring.Dapr.Auth.CallbackTokenIssuer(signingKeyProvider, options, timeProvider)
{
    /// <summary>Claim name carrying the tenant id (canonical no-dash hex).</summary>
    public const string TenantIdClaim = CallbackTokenClaimNames.TenantId;

    /// <summary>Claim name carrying the canonical agent / unit address (e.g. <c>unit:&lt;hex&gt;</c>).</summary>
    public const string AgentAddressClaim = CallbackTokenClaimNames.AgentAddress;

    /// <summary>Claim name carrying the thread id (canonical no-dash hex).</summary>
    public const string ThreadIdClaim = CallbackTokenClaimNames.ThreadId;

    /// <summary>Claim name carrying the inbound message id (canonical no-dash hex).</summary>
    public const string MessageIdClaim = CallbackTokenClaimNames.MessageId;
}