// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Wire-level representation of a unit's manifest-persisted
/// <c>execution:</c> block (#601 / #603 / #409 B-wide). Mirrors the
/// manifest's <see cref="Cvoya.Spring.Manifest.ExecutionManifest"/>
/// shape so the same YAML fragment authored in a unit manifest
/// round-trips through the dedicated
/// <c>GET/PUT/DELETE /api/v1/units/{id}/execution</c> endpoint without
/// renaming.
/// </summary>
/// <remarks>
/// Every field is independently nullable: a unit can declare any
/// subset. Resolution chain (see <c>docs/architecture/units.md</c>):
/// agent.X → unit.X → fail-clean at dispatch / save time.
/// <para>
/// #1732: <c>tool</c> is no longer threaded through the wire shape — the
/// execution tool is derived 1:1 from <see cref="Agent"/> via the runtime
/// registry's <c>IAgentRuntime.Kind</c>. The read-only
/// <see cref="Kind"/> field on the response captures the derived value
/// for portal / CLI display.
/// </para>
/// <para>
/// <see cref="Provider"/> and <see cref="Model"/> are meaningful only when
/// the resolved <see cref="Kind"/> = <c>spring-voyage</c> — the portal
/// hides them for other tool kinds (#598 gating).
/// </para>
/// </remarks>
/// <param name="Image">Default container image reference.</param>
/// <param name="Runtime">Default container runtime (<c>docker</c> / <c>podman</c>).</param>
/// <param name="Provider">Default LLM model provider (Spring Voyage Agent–specific).</param>
/// <param name="Model">Default model identifier (Spring Voyage Agent–specific).</param>
/// <param name="Agent">Agent-runtime registry id (e.g. <c>ollama</c>, <c>claude</c>, <c>openai</c>). The dispatcher resolves this through <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntimeRegistry"/> to pick the launcher.</param>
/// <param name="Kind">
/// Read-only registry-derived execution tool kind (e.g. <c>claude-code-cli</c>,
/// <c>spring-voyage</c>). Populated by the server from
/// <c>IAgentRuntime.Kind</c> when <see cref="Agent"/> resolves; <c>null</c>
/// otherwise. Always <c>null</c> on request bodies — clients cannot set this
/// field.
/// </param>
public record UnitExecutionResponse(
    string? Image = null,
    string? Runtime = null,
    string? Provider = null,
    string? Model = null,
    string? Agent = null,
    string? Kind = null);

/// <summary>
/// Wire-level representation of an agent's <c>execution:</c> block on
/// the <c>AgentDefinitions.Definition</c> document (#601 / #603 / #409
/// B-wide). Mirrors <see cref="UnitExecutionResponse"/> plus the
/// agent-owned <c>hosting</c> field (<c>ephemeral</c> or
/// <c>persistent</c>).
/// </summary>
/// <remarks>
/// The response shape represents the agent's <b>own declared</b>
/// execution block on disk. It does NOT include inherited values from
/// the parent unit — consult the portal / CLI "effective" surface for
/// that post-merge view. When a field is <c>null</c> here it is either
/// unset on the agent or will inherit from the unit at dispatch time.
/// <para>
/// #1732: <c>tool</c> is no longer threaded through the wire shape — the
/// execution tool is derived 1:1 from <see cref="Agent"/> via the runtime
/// registry's <c>IAgentRuntime.Kind</c>. The read-only
/// <see cref="Kind"/> field is populated by the server when the agent's
/// declared <see cref="Agent"/> resolves.
/// </para>
/// </remarks>
/// <param name="Kind">
/// Read-only registry-derived execution tool kind. Server-populated; clients
/// cannot set this field on request bodies.
/// </param>
public record AgentExecutionResponse(
    string? Image = null,
    string? Runtime = null,
    string? Provider = null,
    string? Model = null,
    string? Hosting = null,
    string? Agent = null,
    string? Kind = null);