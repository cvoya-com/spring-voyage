// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Runtime;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Per-invocation callback token claim shape, exchanged between the runtime
/// launcher (issuer) and the dispatcher's orchestration callback API
/// (validator). Scopes a single inbound invocation to one tenant, one agent
/// address, one thread, and one message; expires when the invocation ends.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0039 §3 "Authorization rules — the SDK is unit-callable only,"
/// every callback into the dispatcher carries a token that the validator
/// integrity-checks before any handler runs. The validator does not consult
/// the directory; the address-scheme gate, target-is-direct-child gate,
/// self-delegation gate, depth gate, and tenant-containment gate live in
/// the endpoint handler (D13).
/// </para>
/// <para>
/// This record is the wire-shape, free of JWT concerns. The issuer
/// (<c>ICallbackTokenIssuer</c>) signs the claim shape into a compact JWT; the validator
/// (<c>Cvoya.Spring.Dispatcher.Auth.CallbackTokenValidator</c>) returns the
/// same shape after verifying the signature, the expiry, and the claim
/// presence. Keeping the record in <c>Cvoya.Spring.Core</c> lets both halves
/// of the round-trip — and any future SDK shape that needs to inspect the
/// token without depending on the dispatcher project — share one type.
/// </para>
/// </remarks>
/// <param name="TenantId">
/// The tenant the inbound invocation is scoped to. Every callback the runtime
/// makes during this invocation must resolve against this tenant; the
/// endpoint handler enforces cross-tenant containment by comparing this
/// claim against the resolved caller's tenant on every call.
/// </param>
/// <param name="AgentAddress">
/// The agent (or unit) address the runtime is invoked for. The SDK is
/// structurally unit-callable only — endpoint handlers reject
/// <c>agent://</c>-shaped addresses with 403
/// <c>OrchestrationCallerIsNotUnit</c>. The validator does not enforce the
/// address-scheme rule; it preserves the claim verbatim so the handler can
/// gate on it.
/// </param>
/// <param name="ThreadId">
/// The thread the inbound message belongs to. Every callback during this
/// invocation acts on this thread; the per-thread orchestration depth
/// counter is keyed on it.
/// </param>
/// <param name="MessageId">
/// The inbound message id the invocation is responding to. Recorded on
/// every <see cref="OrchestrationDecision"/> emitted during the turn so
/// auditors can trace the cause of every delegation.
/// </param>
/// <param name="ExpiresAt">
/// Absolute expiry. Tokens are short-lived (issuer default: five minutes;
/// configurable per host). The validator rejects expired tokens before any
/// handler runs.
/// </param>
public sealed record CallbackToken(
    Guid TenantId,
    Address AgentAddress,
    Guid ThreadId,
    Guid MessageId,
    DateTimeOffset ExpiresAt);
