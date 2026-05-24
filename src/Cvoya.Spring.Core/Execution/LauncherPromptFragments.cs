// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Shared system-prompt fragment for the concurrency constraint that
/// applies only to CLI-runtime agents when the agent opts into
/// <c>concurrent_threads: true</c>.
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
/// The fragment lives in <see cref="Cvoya.Spring.Core.Execution"/> rather
/// than the launcher project because both consumers cross assembly
/// boundaries: the CLI-runtime path now delivers the guard via the
/// <see cref="IAgentBootstrapBundleProvider"/>'s assembled-prompt
/// composition (the launcher's system-prompt file —
/// <c>.spring/system-prompt.md</c> for Claude per #2672, <c>AGENTS.md</c>
/// for Codex, and <c>GEMINI.md</c> or <c>.spring/system-prompt.md</c>
/// for Gemini per its <c>system_prompt_mode</c>), and the Spring Voyage
/// agent path delivers it via the launcher's <c>SPRING_SYSTEM_PROMPT</c>
/// env var. Keeping the guard string in a single Core-level class is
/// the single source of truth both consumers can reach.
/// </para>
/// <para>
/// The pre-cutover <c>ResponseDiscipline</c> fragment that used to live
/// here was superseded by the platform contract in
/// <c>PlatformPromptProvider</c> (Layer 1 of the assembled prompt) —
/// every agent, regardless of launcher or skill grants, sees that
/// contract via the assembled system prompt that the bootstrap bundle
/// delivers. Carrying a parallel fragment here would have been two
/// sources of truth for the same constraint.
/// </para>
/// </remarks>
public static class LauncherPromptFragments
{
    /// <summary>
    /// Marker prepended to the user's assembled prompt by every CLI-runtime
    /// system-prompt delivery path when the resolved agent / unit
    /// <c>concurrent_threads</c> flag is <c>true</c>. Single source of
    /// truth so the bootstrap-bundle path (Claude / Codex / Gemini) and
    /// the Spring Voyage launcher's <c>SPRING_SYSTEM_PROMPT</c> path all
    /// emit the same text.
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
    /// Composes the <see cref="ConcurrentThreadsGuard"/> fragment with the
    /// user's assembled prompt body. The guard is only prepended when
    /// <paramref name="concurrentThreads"/> is <c>true</c>; otherwise the
    /// prompt is returned unchanged.
    /// </summary>
    /// <param name="prompt">
    /// The assembled prompt body. May be <c>null</c> or empty — the guard
    /// still emits when concurrent threads are on, so callers who need
    /// it to fire on a sparse prompt get it.
    /// </param>
    /// <param name="concurrentThreads">
    /// The resolved agent / unit <c>concurrent_threads</c> policy value.
    /// </param>
    public static string Compose(string prompt, bool concurrentThreads)
    {
        var body = prompt ?? string.Empty;
        if (concurrentThreads)
        {
            return ConcurrentThreadsGuard + body;
        }
        return body;
    }
}
