// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Execution;

using Cvoya.Spring.Core.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Pin-tests for the <see cref="LauncherPromptFragments"/> guard fragment
/// content (ADR-0041 / the <c>concurrent_conversations: true</c> author
/// contract). The fragment text invariants are pinned here; the
/// delivery channel — the assembler's in-band render inside
/// <c>## Platform Instructions</c> — is covered by the prompt assembler's
/// own targeted tests.
/// </summary>
/// <remarks>
/// #2668: the class moved from <c>Cvoya.Spring.AgentRuntimes.Launchers</c>
/// into <c>Cvoya.Spring.Core.Execution</c> as a public type so the
/// bundle provider (in <c>Cvoya.Spring.Dapr</c>) reaches the single
/// source of truth.
///
/// #2738: the guard is now a <c>### …</c> sub-section of
/// <c>## Platform Instructions</c> rather than a top-level <c>##</c>
/// heading prepended above everything. The legacy
/// <c>## End Spring Voyage runtime guard</c> closing heading was
/// dropped at the same time — markdown section boundaries are implicit.
///
/// #3041: the guard is framed in participant-set / conversation terms —
/// no agent-facing "thread" vocabulary, an opaque platform-managed work
/// subdirectory at <c>$SPRING_WORKSPACE_PATH/work/&lt;id&gt;/</c>, and the
/// symbols renamed from <c>ConcurrentThreadsGuard</c>.
/// </remarks>
public class LauncherPromptFragmentsTests
{
    [Fact]
    public void ConcurrentConversationsGuard_CarriesTheLoadBearingConstraints()
    {
        // #2745: the guard is the universal core — name the two things
        // the platform isolates per conversation (private work directory +
        // session continuity) and the constraints that follow from what is
        // shared. SE-specific tool guidance (watcher commands, broad
        // process kills, "run as one-shot commands") moves into the
        // sv.engineer.defaults bundle and must NOT appear here.
        var guard = LauncherPromptFragments.ConcurrentConversationsGuard;

        guard.ShouldContain("Concurrent conversations — per-conversation isolation");
        guard.ShouldContain("$SPRING_WORKSPACE_PATH/work/<id>/");
        guard.ShouldContain("Work directory");
        guard.ShouldContain("Session continuity");
        // Shared-state framing — ephemeral-port advice and the no-
        // process-global-mutation rule are the two constraints
        // load-bearing for any agent class.
        guard.ShouldContain("ephemeral");
        guard.ShouldContain("port 0");
        guard.ShouldContain("process-global");
    }

    /// <summary>
    /// #3041: no agent-facing "thread" vocabulary survives in the guard —
    /// the conversation is the participant set, and the per-conversation
    /// id is opaque platform plumbing the agent never names.
    /// </summary>
    [Fact]
    public void ConcurrentConversationsGuard_HasNoAgentFacingThreadVocabulary()
    {
        var guard = LauncherPromptFragments.ConcurrentConversationsGuard;

        guard.ToLowerInvariant().ShouldNotContain("thread");
        guard.ShouldNotContain("SPRING_THREAD_ID");
    }

    /// <summary>
    /// #2745: the SE-specific tool names that used to live in the guard
    /// move into the sv.engineer.defaults bundle. The universal core
    /// MUST NOT reference any of them — an analyst, PM, or router agent
    /// has no model for these tools and the prose was signal noise to
    /// them.
    /// </summary>
    [Fact]
    public void ConcurrentConversationsGuard_DoesNotEnumerateEngineerSpecificTools()
    {
        var guard = LauncherPromptFragments.ConcurrentConversationsGuard;

        foreach (var seToolName in new[]
        {
            "pytest --watch",
            "pytest-watch",
            "npm run dev",
            "next dev",
            "vite",
            "cargo watch",
            "nodemon",
            "tail -f",
            "watchman watch",
            "dotnet watch run",
            "pkill -f",
            "killall",
        })
        {
            guard.ShouldNotContain(
                seToolName,
                customMessage: $"`{seToolName}` is engineer-specific; it belongs in sv.engineer.defaults, not the universal guard (#2745).");
        }
    }

    /// <summary>
    /// #2738: the guard heading is a <c>### …</c> sub-section so it nests
    /// cleanly under the assembler's <c>## Platform Instructions</c>
    /// parent. A top-level <c>##</c> heading would re-introduce the
    /// sibling-of-its-own-parent problem the restructure removes.
    /// </summary>
    [Fact]
    public void ConcurrentConversationsGuard_OpensWithLevelThreeHeading()
    {
        var guard = LauncherPromptFragments.ConcurrentConversationsGuard;

        guard.ShouldStartWith("### " + LauncherPromptFragments.ConcurrentConversationsGuardAnchor);
        // No other line in the guard should reintroduce a `##`-or-
        // shallower heading.
        guard.ShouldNotContain("\n## ");
        guard.ShouldNotContain("\n# ");
    }

    /// <summary>
    /// #2738: the legacy <c>## End Spring Voyage runtime guard</c>
    /// closing heading is dropped. Markdown section boundaries are
    /// implicit — the next heading starts the next section.
    /// </summary>
    [Fact]
    public void ConcurrentConversationsGuard_DoesNotEmitClosingHeading()
    {
        var guard = LauncherPromptFragments.ConcurrentConversationsGuard;

        guard.ShouldNotContain("End Spring Voyage runtime guard");
        guard.ShouldNotContain("## End ");
    }
}
