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
/// Python SDK runtimes do not consume this fragment — the SDK author
/// writes their own code and the prompt-guard idea does not apply.
/// </para>
/// </remarks>
internal static class LauncherPromptFragments
{
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
    /// Composes the per-runtime system-prompt fragment with the user's
    /// assembled prompt body when the launcher's
    /// <see cref="AgentLaunchContext.ConcurrentThreads"/> flag is
    /// <c>true</c>. The guard fragment is prepended (not appended /
    /// substituted) so the model sees it as platform-level instructions
    /// before the user's narrative. When the flag is <c>false</c>, the
    /// caller's prompt is returned verbatim.
    /// </summary>
    /// <param name="prompt">
    /// The assembled prompt from <see cref="AgentLaunchContext.Prompt"/>.
    /// May be <c>null</c> or empty — the fragment still emits, so callers
    /// who need the guard to fire on a sparse prompt get it.
    /// </param>
    /// <param name="concurrentThreads">
    /// The resolved <see cref="AgentLaunchContext.ConcurrentThreads"/>
    /// value. Pass straight from the launch context.
    /// </param>
    public static string Compose(string prompt, bool concurrentThreads)
    {
        if (!concurrentThreads)
        {
            return prompt;
        }

        // Prepend so the guard precedes the user's narrative — model
        // attention biases toward what comes first in long contexts.
        return ConcurrentThreadsGuard + (prompt ?? string.Empty);
    }
}
