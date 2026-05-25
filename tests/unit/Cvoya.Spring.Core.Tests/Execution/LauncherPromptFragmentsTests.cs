// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Execution;

using Cvoya.Spring.Core.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Pin-tests for the <see cref="LauncherPromptFragments"/> guard fragment
/// composition (ADR-0041 § "The <c>concurrent_threads: true</c> author
/// contract"). The fragment text invariants are pinned here; the
/// delivery channels (in-band assembler render for the bundle path and
/// the dispatch ephemeral path, Spring Voyage launcher's
/// <c>SPRING_SYSTEM_PROMPT</c> wrap for the deploy persistent path) are
/// covered by their own targeted tests.
/// </summary>
/// <remarks>
/// #2668: the class moved from <c>Cvoya.Spring.AgentRuntimes.Launchers</c>
/// into <c>Cvoya.Spring.Core.Execution</c> as a public type so the
/// bundle provider (in <c>Cvoya.Spring.Dapr</c>) and the launchers (in
/// <c>Cvoya.Spring.AgentRuntimes</c>) both reach the same single source
/// of truth.
///
/// #2738: the guard is now a <c>### …</c> sub-section of
/// <c>## Platform Instructions</c> rather than a top-level <c>##</c>
/// heading prepended above everything. The legacy
/// <c>## End Spring Voyage runtime guard</c> closing heading was
/// dropped at the same time — markdown section boundaries are implicit.
/// <see cref="LauncherPromptFragments.Compose"/> is now idempotent: a
/// prompt that already carries the in-band guard anchor is returned
/// unchanged, so the dispatch ephemeral path's launcher wrap does not
/// double-apply.
/// </remarks>
public class LauncherPromptFragmentsTests
{
    private const string SampleUserPrompt = "## Platform Instructions\n\nBe helpful.";

    [Fact]
    public void ConcurrentThreadsGuard_CarriesTheLoadBearingConstraints()
    {
        // The contract enumerated in ADR-0041 § "The concurrent_threads:
        // true author contract" must be visible verbatim in the guard so
        // grep'ing the assembled prompt (or the runtime's logs) can find
        // each constraint. If a future reword drops one of these terms,
        // this test fails so the deletion is intentional.
        var guard = LauncherPromptFragments.ConcurrentThreadsGuard;

        guard.ShouldContain("Spring Voyage runtime guard");
        guard.ShouldContain("concurrent_threads is on");
        guard.ShouldContain("pytest --watch");
        guard.ShouldContain("npm run dev");
        guard.ShouldContain("ephemeral");
        guard.ShouldContain("$SPRING_WORKSPACE_PATH/threads/$SPRING_THREAD_ID/");
        guard.ShouldContain("pkill -f pytest");
    }

    /// <summary>
    /// #2738: the guard heading is a <c>### …</c> sub-section so it nests
    /// cleanly under the assembler's <c>## Platform Instructions</c>
    /// parent. A top-level <c>##</c> heading would re-introduce the
    /// sibling-of-its-own-parent problem the restructure removes.
    /// </summary>
    [Fact]
    public void ConcurrentThreadsGuard_OpensWithLevelThreeHeading()
    {
        var guard = LauncherPromptFragments.ConcurrentThreadsGuard;

        guard.ShouldStartWith("### " + LauncherPromptFragments.ConcurrentThreadsGuardAnchor);
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
    public void ConcurrentThreadsGuard_DoesNotEmitClosingHeading()
    {
        var guard = LauncherPromptFragments.ConcurrentThreadsGuard;

        guard.ShouldNotContain("End Spring Voyage runtime guard");
        guard.ShouldNotContain("## End ");
    }

    [Fact]
    public void Compose_ConcurrentThreadsFalse_ReturnsPromptUnchanged()
    {
        // After the silent-dispatch cutover the universal response-
        // discipline guard moved to the platform-layer contract emitted
        // by IPlatformPromptProvider and assembled into the per-agent
        // system prompt. The composer no longer prepends anything when
        // concurrent_threads is off — the prompt is the unmodified
        // assembled body.
        var composed = LauncherPromptFragments.Compose(SampleUserPrompt, concurrentThreads: false);

        composed.ShouldBe(SampleUserPrompt);
    }

    [Fact]
    public void Compose_ConcurrentThreadsTrue_PrependsGuardWhenBodyHasNoneYet()
    {
        // The deploy-persistent SVA path delivers raw
        // definition.Instructions (no `## Platform Instructions`
        // section to nest into), so the wrap prepends the guard.
        var rawInstructions = "Be helpful.";
        var composed = LauncherPromptFragments.Compose(rawInstructions, concurrentThreads: true);

        composed.ShouldStartWith("### " + LauncherPromptFragments.ConcurrentThreadsGuardAnchor);
        composed.ShouldEndWith(rawInstructions);
        composed.ShouldContain(rawInstructions, Case.Sensitive);
        composed.IndexOf("concurrent_threads is on", StringComparison.Ordinal)
            .ShouldBeLessThan(composed.IndexOf(rawInstructions, StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_EmptyPrompt_ReturnsEmptyWhenConcurrentThreadsOff()
    {
        var composed = LauncherPromptFragments.Compose(string.Empty, concurrentThreads: false);

        composed.ShouldBe(string.Empty);
    }

    [Fact]
    public void Compose_EmptyPrompt_EmitsConcurrentThreadsGuardWhenOn()
    {
        // A sparse / empty prompt still gets the concurrent-threads guard
        // when the agent opts in.
        var composed = LauncherPromptFragments.Compose(string.Empty, concurrentThreads: true);

        composed.ShouldStartWith("### " + LauncherPromptFragments.ConcurrentThreadsGuardAnchor);
    }

    /// <summary>
    /// #2738: the assembler now renders the guard in-band inside
    /// <c>## Platform Instructions</c>. The dispatch ephemeral path's
    /// launcher wrap (Spring Voyage agent's SPRING_SYSTEM_PROMPT)
    /// runs against that already-assembled body — Compose must be a
    /// no-op when the body already carries the guard's distinctive
    /// anchor, otherwise the runtime sees two copies of the guard.
    /// </summary>
    [Fact]
    public void Compose_IsIdempotent_WhenPromptAlreadyContainsTheInBandGuard()
    {
        var assembled = "## Platform Instructions\n\n"
            + "### About Spring Voyage\n\n"
            + "...intro...\n\n"
            + LauncherPromptFragments.ConcurrentThreadsGuard
            + "\n\n## Role-specific instructions\n\nbody";

        var composed = LauncherPromptFragments.Compose(assembled, concurrentThreads: true);

        composed.ShouldBe(assembled);
        // The anchor appears exactly once even though Compose was asked
        // to add the guard.
        var first = composed.IndexOf(LauncherPromptFragments.ConcurrentThreadsGuardAnchor, StringComparison.Ordinal);
        var second = composed.IndexOf(LauncherPromptFragments.ConcurrentThreadsGuardAnchor, first + 1, StringComparison.Ordinal);
        second.ShouldBe(-1);
    }
}
