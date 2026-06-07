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
    /// <param name="inboundMessage">
    /// The representative message for the dispatch — the batch head
    /// (<c>batch[0]</c>) when <paramref name="batch"/> is supplied. Drives
    /// routing (<c>To</c>/<c>ThreadId</c>), the A2A correlation id, and the
    /// per-turn MCP session.
    /// </param>
    /// <param name="context">
    /// The prompt-assembly context (unit members, policies, skills, prior messages,
    /// agent instructions) the caller has already assembled. May be <c>null</c>
    /// when the dispatcher should render only the platform prompt layer.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <param name="batch">
    /// The full ordered set of pending messages to deliver to the runtime in
    /// this single turn (#3056), oldest-first and sharing one thread. The
    /// inbound envelope names every message in the set so the runtime reasons
    /// over the net current state rather than a stale prefix. <c>null</c> (or a
    /// single-element list) is a one-message turn — the envelope renders
    /// exactly as it did pre-#3056.
    /// </param>
    Task<RuntimeOutcome> DispatchAsync(
        Message inboundMessage,
        PromptAssemblyContext? context,
        CancellationToken ct = default,
        IReadOnlyList<Message>? batch = null);
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

    /// <summary>
    /// Conventional key on <see cref="Diagnostics"/> carrying the turn's total
    /// cost in USD (decimal), when the runtime reported one — e.g. the Claude
    /// Code CLI's <c>total_cost_usd</c>, captured by the sidecar and read off
    /// the A2A task metadata (#3073). Absent when the runtime produced no cost
    /// signal; the dispatch coordinator emits a <c>CostIncurred</c> activity
    /// only when this key is present and positive.
    /// </summary>
    public const string CostUsdKey = "costUsd";

    /// <summary>Conventional <see cref="Diagnostics"/> key: input tokens consumed this turn (int).</summary>
    public const string InputTokensKey = "inputTokens";

    /// <summary>Conventional <see cref="Diagnostics"/> key: output tokens generated this turn (int).</summary>
    public const string OutputTokensKey = "outputTokens";

    /// <summary>Conventional <see cref="Diagnostics"/> key: the model id the turn billed against (string).</summary>
    public const string ModelKey = "model";

    /// <summary>
    /// Conventional key on <see cref="Diagnostics"/> carrying the tool / skill
    /// names a CLI-runtime turn invoked, as the sidecar's stream-json parser
    /// observed them (#3124). Value is an ordered list of names
    /// (<c>IReadOnlyList&lt;string&gt;</c>); the dispatch coordinator emits one
    /// <c>ToolCall</c> activity per name so a CLI-runtime invocation
    /// (<c>claude-code</c>, <c>gemini</c>, <c>codex</c>) is observable in the
    /// portal Activity feed and via <c>spring tail</c>. Absent for runtimes
    /// that surface no tool calls (text-mode, or a turn that called none).
    /// Distinct from <see cref="ToolCallCountKey"/>, which is the worker-side
    /// MCP session count used to decide silent-completion.
    /// </summary>
    public const string StreamToolCallsKey = "streamToolCalls";

    /// <summary>
    /// Conventional key on <see cref="Diagnostics"/> carrying a CLI-runtime
    /// turn's captured stderr text (#3124), surfaced by the sidecar as a
    /// dedicated <c>stderr</c> A2A artifact. The dispatch coordinator emits it
    /// as a <c>RuntimeLog</c> activity (at <c>Warning</c>) so non-structured
    /// runtime output is never silently dropped from the Activity feed. Absent
    /// when the turn produced no stderr.
    /// </summary>
    public const string RuntimeStderrKey = "runtimeStderr";
}
