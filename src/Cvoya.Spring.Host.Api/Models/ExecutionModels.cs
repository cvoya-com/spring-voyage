// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Wire-level representation of a unit's manifest-persisted
/// <c>execution:</c> block under ADR-0038. Carries the agent-runtime
/// catalogue id and a structured <c>{provider, id}</c> model selector;
/// the legacy flat <c>provider</c> / <c>agent</c> / <c>kind</c> fields
/// are gone.
/// </summary>
/// <remarks>
/// <para>
/// Every field is independently nullable: a unit can declare any
/// subset. Resolution chain (see <c>docs/architecture/units.md</c>):
/// agent.X → unit.X → fail-clean at dispatch / save time.
/// </para>
/// <para>
/// Per ADR-0039 §7 the <c>containerRuntime</c> slot is gone — the
/// container runtime (<c>docker</c> / <c>podman</c>) is platform
/// configuration, picked once by the host process at deploy time.
/// </para>
/// <para>
/// Example (multi-provider runtime):
/// <code>
/// {
///   "image": "ghcr.io/example/agent:latest",
///   "runtime": "spring-voyage",
///   "model": { "provider": "ollama", "id": "llama3.2:3b" }
/// }
/// </code>
/// </para>
/// <para>
/// Example (fixed-provider runtime):
/// <code>
/// {
///   "image": "ghcr.io/example/claude:latest",
///   "runtime": "claude-code",
///   "model": { "provider": "anthropic", "id": "claude-opus-4-8" }
/// }
/// </code>
/// </para>
/// </remarks>
/// <param name="Image">Default container image reference.</param>
/// <param name="Runtime">
/// Agent-runtime catalogue id (ADR-0038): <c>claude-code</c>, <c>codex</c>,
/// <c>gemini</c>, <c>spring-voyage</c>, or a future custom runtime declared
/// in <c>eng/runtime-catalog/runtime-catalog.yaml</c>. The dispatcher resolves this
/// through <see cref="Cvoya.Spring.Core.Catalog.IRuntimeCatalog"/> to pick
/// the launcher.
/// </param>
/// <param name="Model">
/// Structured <c>{provider, id}</c> model selector. The provider is
/// intrinsic to the model; there is no separate top-level
/// <c>provider</c> slot.
/// </param>
/// <param name="SystemPromptMode">
/// How the platform-assembled system prompt combines with the runtime's
/// own default system prompt (#2692 / #2691 / #2667). Wire form is the
/// lower-case enum literal — exactly one of <c>"append"</c> or
/// <c>"replace"</c>. <c>null</c> on PUT means "leave the persisted value
/// alone"; <c>null</c> on the GET response means "no unit-level default
/// declared". Validation rejects any other literal with a 400.
/// </param>
public record UnitExecutionResponse(
    string? Image = null,
    string? Runtime = null,
    AiModelDto? Model = null,
    string? SystemPromptMode = null);

/// <summary>
/// Wire-level representation of an agent's <c>execution:</c> block on
/// the <c>AgentDefinitions.Definition</c> document. Mirrors
/// <see cref="UnitExecutionResponse"/> plus the agent-owned
/// <c>hosting</c> field (<c>ephemeral</c> or <c>persistent</c>).
/// </summary>
/// <remarks>
/// <para>
/// The response shape represents the agent's <b>own declared</b>
/// execution block on disk. It does NOT include inherited values from
/// the parent unit — consult the portal / CLI "effective" surface for
/// that post-merge view. When a field is <c>null</c> here it is either
/// unset on the agent or will inherit from the unit at dispatch time.
/// </para>
/// <para>
/// Per ADR-0039 §7 the <c>containerRuntime</c> slot is gone — the
/// container runtime (<c>docker</c> / <c>podman</c>) is platform
/// configuration.
/// </para>
/// </remarks>
/// <param name="Image">Default container image reference.</param>
/// <param name="Runtime">Agent-runtime catalogue id (ADR-0038).</param>
/// <param name="Model">Structured <c>{provider, id}</c> model selector.</param>
/// <param name="Hosting">Hosting mode (<c>ephemeral</c> or <c>persistent</c>).</param>
/// <param name="ConcurrentThreads">
/// Read-only projection of the agent's <c>execution.concurrent_threads</c>
/// slot from the persisted definition JSON (#2096 / ADR-0041). Surfaces
/// as <c>true</c> when the agent has explicitly opted in to per-thread
/// in-container concurrency (with the published author contract), and as
/// <c>false</c> when the agent uses the safe-default serialised mailbox.
/// <c>null</c> when the agent has no execution block at all on disk —
/// the dispatcher uses the runtime's record default in that case.
/// PUT does not currently consume this field; clients persist it through
/// the <c>--definition</c> / <c>--definition-file</c> paths on agent
/// create. Tracked for first-class wire-write support under #2090.
/// </param>
/// <param name="SystemPromptMode">
/// How the platform-assembled system prompt combines with the runtime's
/// own default (#2692 / #2691 / #2667). Wire form is the lower-case enum
/// literal — exactly one of <c>"append"</c> or <c>"replace"</c>.
/// <c>null</c> on PUT means "leave the persisted value alone"; <c>null</c>
/// on the GET response means "no agent-level declaration — the dispatcher
/// will inherit from the parent unit's default". Validation rejects any
/// other literal with a 400.
/// </param>
public record AgentExecutionResponse(
    string? Image = null,
    string? Runtime = null,
    AiModelDto? Model = null,
    string? Hosting = null,
    bool? ConcurrentThreads = null,
    string? SystemPromptMode = null);

/// <summary>
/// Structured <c>{provider, id}</c> model selector under ADR-0038.
/// Provider is intrinsic to the model; the matching JSON shape is
/// <c>{ "provider": "...", "id": "..." }</c>.
/// </summary>
/// <param name="Provider">
/// Provider id from <c>eng/runtime-catalog/runtime-catalog.yaml</c>:
/// <c>anthropic</c>, <c>openai</c>, <c>google</c>, <c>ollama</c>, …
/// </param>
/// <param name="Id">Provider-scoped model id (e.g. <c>claude-opus-4-8</c>).</param>
public record AiModelDto(
    string Provider,
    string Id);
