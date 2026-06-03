// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Shared system-prompt fragment naming the platform's per-conversation
/// isolation guarantees when an agent opts into
/// <c>concurrent_conversations: true</c>.
/// </summary>
/// <remarks>
/// <para>
/// The CLI-runtime path runs several invocations inside one process space
/// when the agent opts in to <c>concurrent_conversations: true</c>
/// (ADR-0041). Each concurrent instance handles a message sent to a
/// different set of participants — a different conversation. The platform
/// isolates each instance by giving it its own private work subdirectory
/// and its own session-resume slot; everything else — environment, ports,
/// shared caches — is process-global and races against every other live
/// instance. The guard names the two things the platform DOES isolate (so
/// the agent knows where to put conversation-local state) and what stays
/// shared (so the agent knows where it can collide with itself).
/// </para>
/// <para>
/// "thread" is an internal platform concept; agents address and reason
/// about a conversation by its participant set, never by a thread id
/// (ADR-0030 / #2747 / #3041). The per-conversation work subdirectory is
/// keyed on an opaque, platform-managed id that means nothing to the
/// agent.
/// </para>
/// <para>
/// Per #2745 the guard is the universal core: no
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
/// <c>Cvoya.Spring.Dapr</c>) reaches a single source of truth. Per
/// #2738 the assembler renders the fragment in-band as a <c>### …</c>
/// sub-section of <c>## Platform Instructions</c> when the
/// per-invocation <c>PromptAssemblyContext.ConcurrentConversationsGuard</c>
/// flag is <c>true</c>. #2734 retired the legacy
/// <c>LauncherPromptFragments.Compose</c> helper that wrapped the
/// Spring Voyage Agent launcher's <c>SPRING_SYSTEM_PROMPT</c> body —
/// every launcher now consumes the assembled prompt as a bundle file
/// so the in-band render is the single delivery channel.
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
    /// shared by the assembler's in-band render. Tests pin against this
    /// anchor without coupling to the surrounding prose.
    /// </summary>
    public const string ConcurrentConversationsGuardAnchor =
        "Concurrent conversations — per-conversation isolation";

    /// <summary>
    /// The platform-emitted runtime guard for agents that opt in to
    /// <c>concurrent_conversations: true</c>. Names the two things the
    /// platform isolates per conversation (private work subdirectory +
    /// session-resume slot) and what stays shared between concurrent
    /// instances. Per #2745 the universal core lives here;
    /// software-engineering-specific guidance (long-running watcher
    /// tools, broad process kills, "run as one-shot commands") ships in
    /// the <c>sv.engineer.defaults</c> skill bundle.
    /// </summary>
    public const string ConcurrentConversationsGuard =
        "### " + ConcurrentConversationsGuardAnchor + "\n\n" +
        "The platform may run several instances of you at once, each handling a message sent " +
        "to a different set of participants — a different conversation. The platform isolates " +
        "two things per instance; everything else is shared with every concurrent instance.\n\n" +
        "**Isolated per conversation:**\n\n" +
        "- **Work directory.** Each instance has its own private subdirectory at " +
        "`$SPRING_WORKSPACE_PATH/work/<id>/`. The `<id>` segment is opaque and platform-managed " +
        "— it means nothing to you, so never parse it or use it to address another conversation. " +
        "Writes there are private to this instance; writes anywhere else under " +
        "`$SPRING_WORKSPACE_PATH` are visible to every other instance and may race with their " +
        "writes.\n" +
        "- **Session continuity.** Your runtime's session-resume state (conversation context " +
        "across turns) is managed for you automatically — you do not manage it yourself, and you " +
        "do not address other conversations through it.\n\n" +
        "**Shared across instances (the platform does NOT isolate these):**\n\n" +
        "- Environment variables, working directory, process-global signal handlers.\n" +
        "- TCP / UDP ports, the filesystem outside your private work directory, `/tmp`, `~/.cache`.\n" +
        "- Any child process you start.\n\n" +
        "Two constraints follow directly from what is shared:\n\n" +
        "- If a turn needs to bind a socket, allocate an ephemeral port (port 0) and read the " +
        "assigned port back — fixed ports collide between concurrent instances.\n" +
        "- Do not mutate process-global state (env vars, working directory at the top level, " +
        "signal handlers) without restoring it before the turn ends.";
}
