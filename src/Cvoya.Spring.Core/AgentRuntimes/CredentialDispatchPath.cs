// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Identifies which consumer path will dispatch a stored credential.
/// A single tenant/unit secret may be accepted by one dispatch path and
/// rejected by another (for example, Anthropic accepts both
/// <c>sk-ant-api…</c> API keys and <c>sk-ant-oat…</c> OAuth tokens through
/// the <c>claude</c> CLI — the in-container agent-runtime path — but the
/// Anthropic Platform REST endpoint only accepts the API-key form).
/// The credential-status probe uses this value so callers can ask the
/// platform the right question: <i>will the dispatch path I'm about to
/// use accept the stored credential?</i>
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="IAgentRuntime"/> declares its per-path acceptance rules
/// via <see cref="IAgentRuntime.IsCredentialFormatAccepted"/>. The
/// architectural boundary between these paths is documented in
/// ADR 0021 — <c>Cvoya.Spring.Core</c> never implements a multi-turn loop
/// itself, so both paths are thin adapters over either the provider's
/// REST surface (for single-shot completions) or the agent-runtime CLI
/// inside a container (for full agent dispatch).
/// </para>
/// </remarks>
public enum CredentialDispatchPath
{
    /// <summary>
    /// The host-side REST path consumed by
    /// <see cref="Cvoya.Spring.Core.Execution.IAiProvider"/> for
    /// single-shot completions and streaming. This path targets the
    /// provider's Messages/Chat endpoint directly and therefore only
    /// accepts credential formats the endpoint honours.
    /// </summary>
    Rest = 0,

    /// <summary>
    /// The in-container agent-runtime path consumed by
    /// <see cref="IAgentRuntime"/> via the <c>A2AExecutionDispatcher</c>.
    /// This path runs the provider-specific CLI or SDK inside the unit's
    /// container, which may accept a broader set of credential formats
    /// than the REST path (for example, the <c>claude</c> CLI accepts
    /// Claude.ai OAuth tokens).
    /// </summary>
    AgentRuntime = 1,
}