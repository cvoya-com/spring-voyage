// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

/// <summary>
/// Strongly-typed result for
/// <see cref="OrchestrationToolHandlers.HandleQueryStatusAsync"/>,
/// matching the <c>query_status.output.schema.json</c> shape from
/// ADR-0039 §3.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Status"/> is required and reports the target's lifecycle
/// state mapped onto the closed schema enum
/// (<c>ready | busy | stopped | error | unknown</c>). The other two
/// fields are optional per the schema and are omitted from the wire
/// envelope when they cannot be determined cheaply.
/// </para>
/// <para>
/// <see cref="LastActivityAt"/> is intentionally <c>null</c> in the v0.1
/// dispatcher-side implementation: the handler runs in the dispatcher
/// process where <see cref="Cvoya.Spring.Core.Observability.IActivityQueryService"/>
/// is not registered, so a real "latest activity timestamp" lookup would
/// require either an extra Dapr round-trip per probe or piggy-backing the
/// timestamp onto the worker-side <c>StatusQuery</c> response. The schema
/// explicitly allows <c>null</c> to express "not known" rather than
/// fabricating a timestamp; v0.1 takes that path. A follow-up may extend
/// the worker-side <c>StatusQuery</c> payload to include the latest
/// activity timestamp so the probe stays single-round-trip; the handler
/// reads the field opportunistically.
/// </para>
/// </remarks>
/// <param name="Status">Lifecycle status (ready / busy / stopped / error / unknown).</param>
/// <param name="LastActivityAt">ISO-8601 timestamp of the target's most recent activity; <c>null</c> when not known.</param>
/// <param name="BusyOnThread">Thread id the target is currently busy on; <c>null</c> when idle or not known.</param>
public sealed record MemberStatusResult(
    string Status,
    DateTimeOffset? LastActivityAt = null,
    string? BusyOnThread = null);
