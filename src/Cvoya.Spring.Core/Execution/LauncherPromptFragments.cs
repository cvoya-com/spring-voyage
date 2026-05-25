// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Shared system-prompt fragment naming the platform's per-thread
/// isolation guarantees when an agent opts into
/// <c>concurrent_threads: true</c>.
/// </summary>
/// <remarks>
/// <para>
/// The CLI-runtime path runs N concurrent invocations inside one
/// process space when the agent opts in to <c>concurrent_threads:
/// true</c> (ADR-0041 § "Two modes, bound to
/// <c>concurrent_threads</c>"). The platform isolates each thread by
/// giving it its own workspace subtree and its own session-storage
/// slot; everything else — environment, ports, shared caches — is
/// process-global and races against every other live thread. The guard
/// names the two things the platform DOES isolate (so the agent knows
/// where to put thread-local state) and what stays shared (so the
/// agent knows where it can collide with itself).
/// </para>
/// <para>
/// Per #2745 the guard was rewritten as the universal core: no
/// software-engineering-specific tool names (the previous fragment
/// enumerated <c>pytest --watch</c>, <c>npm run dev</c>, etc., which
/// generalise poorly to PM / analyst / router agents). SE-shaped
/// guidance — the watcher-tool examples, broad process kills, the
/// "run as one-shot commands" advice — ships in the
/// <c>sv.engineer.defaults</c> bundle that engineer-shaped agents
/// equip on top of the platform contract. The platform layer stays
/// universal; engineer-shaped concerns are an additive bundle.
/// </para>
/// <para>
/// The fragment lives in <see cref="Cvoya.Spring.Core.Execution"/> as
/// a dep-free constant so the prompt assembler (in
/// <c>Cvoya.Spring.Dapr</c>) and the Spring Voyage agent launcher (in
/// <c>Cvoya.Spring.AgentRuntimes</c>) both reach the same single
/// source of truth. Per #2738 the assembler renders the fragment
/// in-band as a <c>### …</c> sub-section of <c>## Platform
/// Instructions</c> when the per-invocation
/// <c>PromptAssemblyContext.ConcurrentThreadsGuard</c> flag is
/// <c>true</c>; <see cref="Compose"/> still wraps the Spring Voyage
/// agent's deploy-time <c>SPRING_SYSTEM_PROMPT</c> body (which is raw
/// <c>definition.Instructions</c>, no platform-instructions section
/// to nest into) and is a no-op when the body already carries the
/// in-band guard.
/// </para>
/// <para>
/// The pre-cutover <c>ResponseDiscipline</c> fragment that used to
/// live here was superseded by the platform contract in
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
        "Concurrent threads — per-thread isolation";

    /// <summary>
    /// The platform-emitted runtime guard for agents that opt in to
    /// <c>concurrent_threads: true</c>. Names the two things the
    /// platform isolates per thread (workspace subtree + session
    /// storage) and what stays shared between concurrent turns. Per
    /// #2745 the universal core lives here; software-engineering-
    /// specific guidance (long-running watcher tools, broad process
    /// kills, "run as one-shot commands") ships in the
    /// <c>sv.engineer.defaults</c> skill bundle.
    /// </summary>
    public const string ConcurrentThreadsGuard =
        "### " + ConcurrentThreadsGuardAnchor + "\n\n" +
        "You may have several Spring Voyage threads in flight at once. The platform isolates " +
        "two things per thread; everything else is shared with every concurrent turn.\n\n" +
        "**Isolated per thread:**\n\n" +
        "- **Workspace subtree.** Each thread has its own subdirectory at " +
        "`$SPRING_WORKSPACE_PATH/threads/$SPRING_THREAD_ID/`. Writes there are private to this " +
        "thread; writes anywhere else under `$SPRING_WORKSPACE_PATH` are visible to every other " +
        "thread and may race with their writes.\n" +
        "- **Session storage.** Your runtime's session-resume state (conversation context " +
        "across turns) is keyed on the thread id automatically — you do not manage it yourself, " +
        "and you do not address other threads through it.\n\n" +
        "**Shared across threads (the platform does NOT isolate these):**\n\n" +
        "- Environment variables, working directory, process-global signal handlers.\n" +
        "- TCP / UDP ports, the filesystem outside the per-thread subtree, `/tmp`, `~/.cache`.\n" +
        "- Any child process you start.\n\n" +
        "Two constraints follow directly from what is shared:\n\n" +
        "- If a turn needs to bind a socket, allocate an ephemeral port (port 0) and read the " +
        "assigned port back — fixed ports collide between concurrent turns.\n" +
        "- Do not mutate process-global state (env vars, working directory at the top level, " +
        "signal handlers) without restoring it before the turn ends.";

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
