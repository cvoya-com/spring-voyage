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
/// The fragment lives in <see cref="Cvoya.Spring.Core.Execution"/> as a
/// dep-free constant so the prompt assembler (in <c>Cvoya.Spring.Dapr</c>)
/// and the Spring Voyage agent launcher (in
/// <c>Cvoya.Spring.AgentRuntimes</c>) both reach the same single source
/// of truth. Per #2738 the assembler renders the fragment in-band as a
/// <c>### …</c> sub-section of <c>## Platform Instructions</c> when the
/// per-invocation <c>PromptAssemblyContext.ConcurrentThreadsGuard</c>
/// flag is <c>true</c>; <see cref="Compose"/> still wraps the Spring
/// Voyage agent's deploy-time <c>SPRING_SYSTEM_PROMPT</c> body (which
/// is raw <c>definition.Instructions</c>, no platform-instructions
/// section to nest into) and is a no-op when the body already carries
/// the in-band guard.
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
    /// Distinctive heading-line anchor (without the leading <c>### </c>)
    /// shared by the assembler's in-band render and <see cref="Compose"/>'s
    /// idempotence check. Tests pin against this anchor without coupling
    /// to the surrounding prose.
    /// </summary>
    public const string ConcurrentThreadsGuardAnchor =
        "Spring Voyage runtime guard — concurrent_threads is on";

    /// <summary>
    /// The platform-emitted runtime guard for CLI-runtime agents that opt
    /// in to <c>concurrent_threads: true</c>. Rendered as a <c>### …</c>
    /// sub-section of the assembler's <c>## Platform Instructions</c>
    /// section per #2738; the legacy <c>## End Spring Voyage runtime
    /// guard</c> closing heading was dropped at the same time because
    /// markdown section boundaries are implicit.
    /// </summary>
    public const string ConcurrentThreadsGuard =
        "### " + ConcurrentThreadsGuardAnchor + "\n\n" +
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
        "primitive (`thread.id` is the session id) — do not background-process it.";

    /// <summary>
    /// Wraps a prompt body the assembler did NOT produce — i.e. the
    /// Spring Voyage agent's deploy-time <c>SPRING_SYSTEM_PROMPT</c>,
    /// which carries only <c>definition.Instructions</c>. When
    /// <paramref name="concurrentThreads"/> is <c>true</c> the guard is
    /// prepended above the raw body; otherwise the body is returned
    /// unchanged. Idempotent: a body that already carries the in-band
    /// guard (rendered inside <c>## Platform Instructions</c> by the
    /// assembler) is returned unchanged so the ephemeral-dispatch
    /// SVA path does not double-apply.
    /// </summary>
    /// <param name="prompt">
    /// The prompt body. May be <c>null</c> or empty — the guard still
    /// emits when concurrent threads are on, so callers who need it to
    /// fire on a sparse prompt get it.
    /// </param>
    /// <param name="concurrentThreads">
    /// The resolved agent / unit <c>concurrent_threads</c> policy value.
    /// </param>
    public static string Compose(string prompt, bool concurrentThreads)
    {
        var body = prompt ?? string.Empty;
        if (!concurrentThreads)
        {
            return body;
        }
        if (body.Contains(ConcurrentThreadsGuardAnchor, StringComparison.Ordinal))
        {
            return body;
        }
        return ConcurrentThreadsGuard + "\n\n" + body;
    }
}
