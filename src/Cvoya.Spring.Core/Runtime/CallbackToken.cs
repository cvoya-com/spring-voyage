// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Runtime;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Per-invocation callback token claim shape. Scopes a single inbound
/// invocation to one tenant, one agent address, one thread, and one message;
/// expires when the invocation ends.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0054 retired the messaging callback surface and folded its per-turn
/// delivery authority into the MCP session token. This record survives as
/// the OTLP-ingest credential (issue #2492): the runtime-launcher pipeline
/// mints it via <c>ICallbackTokenIssuer</c> and stamps it as
/// <c>SPRING_CALLBACK_TOKEN</c>; <c>OtlpCallbackAuthHandler</c> in the API
/// host validates it on the <c>/otlp</c> ingest plane. Migrating OTLP ingest
/// off this token is a tracked follow-up.
/// </para>
/// <para>
/// This record is the wire-shape, free of JWT concerns. The issuer
/// (<c>ICallbackTokenIssuer</c>) signs the claim shape into a compact JWT,
/// validated by the API host's OTLP auth handler. Keeping the record in
/// <c>Cvoya.Spring.Core</c> lets both halves of the round-trip share one type.
/// </para>
/// </remarks>
/// <param name="TenantId">
/// The tenant the inbound invocation is scoped to. Every callback the runtime
/// makes during this invocation must resolve against this tenant; the
/// endpoint handler enforces cross-tenant containment by comparing this
/// claim against the resolved caller's tenant on every call.
/// </param>
/// <param name="AgentAddress">
/// The agent (or unit) address the runtime is invoked for. The platform
/// does not gate callbacks by entity type; agents and units both reach
/// the dispatcher under their own scheme and the membership / self /
/// depth / tenant gates handle authorisation. The validator preserves the
/// claim verbatim.
/// </param>
/// <param name="ThreadId">
/// The thread the inbound message belongs to. Every callback during this
/// invocation acts on this thread; the per-thread hop-depth
/// counter is keyed on it.
/// </param>
/// <param name="MessageId">
/// The inbound message id the invocation is responding to. Recorded on
/// every <see cref="RoutingDecision"/> emitted during the turn so
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
