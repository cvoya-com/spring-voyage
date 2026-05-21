// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Shared system-prompt fragments composed in by CLI-runtime launchers
/// (<see cref="ClaudeCodeLauncher"/>, <see cref="CodexLauncher"/>,
/// <see cref="GeminiLauncher"/>) before the user's prompt body. The
/// fragments encode constraints the platform owns at the runtime layer
/// — they are not optional guidance and they are not user-editable from
/// the manifest.
/// </summary>
/// <remarks>
/// <para>
/// The CLI-runtime path runs N concurrent invocations inside one container
/// when the agent opts in to <c>concurrent_threads: true</c> (ADR-0041 §
/// "Two modes, bound to <c>concurrent_threads</c>"). Long-running watcher
/// commands — <c>pytest --watch</c>, <c>npm run dev</c>, <c>cargo watch</c>,
/// <c>tail -f</c>, etc. — never exit on their own and will pin the
/// container indefinitely. The model needs to be told not to issue them;
/// the contract is documented author-side, but a system-prompt guard
/// catches the case where the model independently decides to invoke one.
/// </para>
/// <para>
/// Issue #2493 added the <see cref="ResponseDiscipline"/> fragment,
/// composed in by *every* launcher unconditionally (including the
/// Spring Voyage Agent launcher used by the Python reference agent).
/// Unlike <see cref="ConcurrentThreadsGuard"/>, response discipline is
/// universal — every triggering message MUST result in a final reply
/// regardless of whether the agent opts into concurrent threads.
/// </para>
/// </remarks>
internal static class LauncherPromptFragments
{
    /// <summary>
    /// Always-on prompt fragment prepended by every launcher before the
    /// user's assembled prompt body, independent of any flag (issue
    /// #2493). The platform's "silent success" failure mode — an agent
    /// performs work but never sends a final reply — is the direct
    /// motivation. The fragment instructs the model to:
    /// <list type="number">
    /// <item><description>send a final reply A2A message for every triggering message;</description></item>
    /// <item><description>emit progress updates via <c>sv.runtime.report_progress</c> for work expected to take more than ~10 seconds;</description></item>
    /// <item><description>send an explicit completion message — including on failure.</description></item>
    /// </list>
    /// The fragment is wrapped in stable header / footer markers so
    /// launcher tests can pin its presence without coupling to the
    /// prose.
    /// </summary>
    public const string ResponseDiscipline =
        "## Spring Voyage runtime guard — response discipline\n\n" +
        "You are a member on Spring Voyage. Every triggering message you receive MUST result in:\n\n" +
        "1. A final reply A2A message addressed to the requester. Use your runtime's reply primitive " +
        "(e.g. `Response(text=..., final=True)` from the SV Agent SDK, or your runtime's equivalent).\n" +
        "2. (For work expected to take more than ~10 seconds) Progress updates via `sv.runtime.report_progress` — " +
        "emit meaningful narrative beats: starting work, tool calls underway, intermediate results, blockers encountered.\n" +
        "3. An explicit completion message. Failure replies are required just as success replies are.\n\n" +
        "Do NOT exit silently after performing work. Silent success is a regression.\n\n" +
        "## End Spring Voyage runtime guard — response discipline\n\n";
    /// <summary>
    /// Marker prepended to the user's assembled prompt by every CLI-runtime
    /// launcher when the resolved agent / unit
    /// <see cref="AgentLaunchContext.ConcurrentThreads"/> flag is
    /// <c>true</c>. Single source of truth so ClaudeCodeLauncher,
    /// CodexLauncher, and GeminiLauncher all emit the same text.
    /// </summary>
    /// <remarks>
    /// The fragment is wrapped in a stable header / footer so launcher
    /// tests can pin its presence without coupling to the prose. Authors
    /// who need to verify the guard ran can grep for the header marker
    /// in the assembled prompt or in the runtime's own logs.
    /// </remarks>
    public const string ConcurrentThreadsGuard =
        "## Spring Voyage runtime guard — concurrent_threads is on\n\n" +
        "This container may be serving multiple Spring Voyage threads at the same time. " +
        "Every per-turn process you launch shares the container's PID namespace, port " +
        "namespace, /tmp, and ~/.cache with every other concurrent turn.\n\n" +
        "Constraints you MUST honour:\n\n" +
        "- Do NOT invoke long-running watchers or interactive servers as foreground commands. " +
        "Examples to avoid: `pytest --watch`, `pytest-watch`, `npm run dev`, `next dev`, " +
        "`vite`, `cargo watch`, `nodemon`, `tail -f`, `watchman watch`, `dotnet watch run`. " +
        "These never exit on their own and pin the container indefinitely.\n" +
        "- Do NOT bind fixed TCP / UDP ports. If a turn needs a server, allocate an ephemeral " +
        "port (port 0) and read the assigned port back.\n" +
        "- Do NOT write outside `$SPRING_WORKSPACE_PATH/threads/$SPRING_THREAD_ID/` for " +
        "thread-local state. Files outside that subtree race against every other concurrent turn.\n" +
        "- Do NOT issue broad process kills. `pkill -f pytest`, `killall node`, and similar " +
        "patterns kill every other concurrent turn's processes too.\n" +
        "- Do NOT mutate process-global state — env vars, working directory (`cd` is fine inside " +
        "a subshell, not at the top level), or signal handlers — without restoring it before " +
        "the turn ends.\n\n" +
        "Run tests, builds, and any tool invocation as one-shot commands that exit when finished. " +
        "If you genuinely need long-lived state across messages, use the runtime's session-resume " +
        "primitive (`thread.id` is the session id) — do not background-process it.\n\n" +
        "## End Spring Voyage runtime guard\n\n";

    /// <summary>
    /// Composes the platform's system-prompt fragments with the user's
    /// assembled prompt body. Order (issue #2493):
    /// <list type="number">
    /// <item><description><see cref="ResponseDiscipline"/> — always prepended.</description></item>
    /// <item><description><see cref="ConcurrentThreadsGuard"/> — appended after when <paramref name="concurrentThreads"/> is <c>true</c>.</description></item>
    /// <item><description>The user's prompt body — last.</description></item>
    /// </list>
    /// Both guards are prepended (not appended / substituted) so the
    /// model sees them as platform-level instructions before the user's
    /// narrative — model attention biases toward what comes first in
    /// long contexts.
    /// </summary>
    /// <param name="prompt">
    /// The assembled prompt from <see cref="AgentLaunchContext.Prompt"/>.
    /// May be <c>null</c> or empty — the guards still emit, so callers
    /// who need them to fire on a sparse prompt get them.
    /// </param>
    /// <param name="concurrentThreads">
    /// The resolved <see cref="AgentLaunchContext.ConcurrentThreads"/>
    /// value. Pass straight from the launch context.
    /// </param>
    public static string Compose(string prompt, bool concurrentThreads)
    {
        var body = prompt ?? string.Empty;
        // Order: ResponseDiscipline first (universal), then
        // ConcurrentThreadsGuard (conditional), then the user's prompt.
        if (concurrentThreads)
        {
            return ResponseDiscipline + ConcurrentThreadsGuard + body;
        }
        return ResponseDiscipline + body;
    }
}
