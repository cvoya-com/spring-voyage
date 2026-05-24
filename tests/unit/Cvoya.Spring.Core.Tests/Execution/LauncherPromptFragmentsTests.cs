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
/// delivery channels (CLI-launcher bundle path + Spring Voyage
/// launcher's <c>SPRING_SYSTEM_PROMPT</c> env var) are covered by their
/// own targeted tests.
/// </summary>
/// <remarks>
/// #2668: the class moved from <c>Cvoya.Spring.AgentRuntimes.Launchers</c>
/// into <c>Cvoya.Spring.Core.Execution</c> as a public type so the
/// bundle provider (in <c>Cvoya.Spring.Dapr</c>) and the launchers (in
/// <c>Cvoya.Spring.AgentRuntimes</c>) both reach the same single source
/// of truth.
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
        guard.ShouldContain("End Spring Voyage runtime guard");
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
    public void Compose_ConcurrentThreadsTrue_PrependsConcurrentThreadsGuardOnly()
    {
        var composed = LauncherPromptFragments.Compose(SampleUserPrompt, concurrentThreads: true);

        composed.ShouldStartWith("## Spring Voyage runtime guard — concurrent_threads is on");
        composed.ShouldEndWith(SampleUserPrompt);
        composed.ShouldContain(SampleUserPrompt, Case.Sensitive);
        // The concurrent-threads guard precedes the user's prompt.
        composed.IndexOf("concurrent_threads is on", StringComparison.Ordinal)
            .ShouldBeLessThan(composed.IndexOf(SampleUserPrompt, StringComparison.Ordinal));
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

        composed.ShouldStartWith("## Spring Voyage runtime guard — concurrent_threads is on");
        composed.ShouldContain("End Spring Voyage runtime guard");
    }
}
