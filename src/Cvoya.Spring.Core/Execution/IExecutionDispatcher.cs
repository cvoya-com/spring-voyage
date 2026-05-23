// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Dispatches work to an external agent runtime (e.g., a container running a
/// configured AI agent tool such as Claude Code or Codex). Spring Voyage does
/// not implement its own agent loop — the dispatcher is a thin wrapper over the
/// process/container spawner.
/// </summary>
/// <remarks>
/// Per <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
/// the dispatcher's contract is <em>"run the runtime; tell me how it
/// terminated"</em>. It does not return a <see cref="Message"/> — every
/// outside-world effect a runtime has flows through platform tool calls
/// (<c>sv.messaging.*</c>, <c>sv.task.*</c>, …). The terminal text the
/// runtime container produced is captured as a reasoning trace for
/// diagnostics, never as a message routed back to anyone.
/// </remarks>
public interface IExecutionDispatcher
{
    /// <summary>
    /// Dispatches a message for execution by an external agent runtime and
    /// returns a lifecycle outcome describing how the runtime terminated.
    /// </summary>
    /// <param name="inboundMessage">The message containing the work to dispatch.</param>
    /// <param name="context">
    /// The prompt-assembly context (unit members, policies, skills, prior messages,
    /// agent instructions) the caller has already assembled. May be <c>null</c>
    /// when the dispatcher should render only the platform prompt layer.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<RuntimeOutcome> DispatchAsync(
        Message inboundMessage,
        PromptAssemblyContext? context,
        CancellationToken ct = default);
}

/// <summary>
/// The lifecycle outcome of a single runtime invocation
/// (<see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see> §3).
/// </summary>
/// <param name="ExitCode">
/// Process / container exit code. <c>0</c> means the runtime exited
/// successfully; any non-zero value means the runtime failed (the platform
/// surfaces this through a <c>RuntimeFailed</c> activity).
/// </param>
/// <param name="Duration">Wall-clock time the runtime ran for.</param>
/// <param name="ReasoningTrace">
/// Captured terminal text from the runtime container (stdout, A2A task
/// reply, file-capture buffer — whichever
/// <see cref="AgentResponseCapture"/> mode the launcher selected). Surfaces
/// as a <c>RuntimeReasoning</c> activity, controlled by the OTLP
/// capture-level knob from
/// <see href="../../../docs/decisions/0054-one-mcp-server-one-execution-host.md">ADR-0054</see>.
/// <c>null</c> when the runtime produced no terminal text or the capture
/// mode is <c>off</c>.
/// </param>
/// <param name="Diagnostics">
/// Free-form bag of dispatcher-specific diagnostic values (e.g. tool-call
/// count observed during the turn, A2A task id, container id). Consumers
/// must treat unknown keys as forward-compatible: new dispatchers may add
/// fields without bumping this contract. Values are JSON-serialisable.
/// </param>
public sealed record RuntimeOutcome(
    int ExitCode,
    TimeSpan Duration,
    string? ReasoningTrace,
    IReadOnlyDictionary<string, object?> Diagnostics)
{
    /// <summary>
    /// Conventional key on <see cref="Diagnostics"/> carrying the number of
    /// MCP tool calls the runtime invoked during the turn. The dispatch
    /// coordinator reads this to decide whether to emit
    /// <c>RuntimeCompletedSilent</c>
    /// (<see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see> §5):
    /// a successful runtime that made zero tool calls produced no observable
    /// effect on the outside world.
    /// </summary>
    public const string ToolCallCountKey = "toolCallCount";
}
