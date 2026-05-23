// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using System.Reflection;

using Cvoya.Spring.AgentRuntimes.Launchers;

using Shouldly;

using Xunit;

/// <summary>
/// Pin-tests for the <c>LauncherPromptFragments</c> guard fragment
/// composition (#2096 / ADR-0041 § "The <c>concurrent_threads: true</c>
/// author contract"). The fragment text itself is checked in three
/// per-launcher tests below — these tests pin the composition rules
/// (prepend, idempotent on <c>false</c>, content invariants).
/// </summary>
public class LauncherPromptFragmentsTests
{
    private const string SampleUserPrompt = "## Platform Instructions\n\nBe helpful.";

    /// <summary>
    /// The internal-by-design class is reached through reflection so the
    /// test stays close to the contract surface (one shared fragment
    /// across every CLI launcher) without forcing the type to be public.
    /// </summary>
    private static readonly Type FragmentsType =
        typeof(ClaudeCodeLauncher).Assembly
            .GetType("Cvoya.Spring.AgentRuntimes.Launchers.LauncherPromptFragments", throwOnError: true)!;

    private static string ConcurrentThreadsGuard =>
        (string)FragmentsType
            .GetField("ConcurrentThreadsGuard", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    private static string ResponseDiscipline =>
        (string)FragmentsType
            .GetField("ResponseDiscipline", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    private static string Compose(string prompt, bool concurrentThreads) =>
        (string)FragmentsType
            .GetMethod("Compose", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { prompt, concurrentThreads })!;

    [Fact]
    public void ConcurrentThreadsGuard_CarriesTheLoadBearingConstraints()
    {
        // The contract enumerated in ADR-0041 § "The concurrent_threads:
        // true author contract" must be visible verbatim in the guard so
        // grep'ing the assembled prompt (or the runtime's logs) can find
        // each constraint. If a future reword drops one of these terms,
        // this test fails so the deletion is intentional.
        var guard = ConcurrentThreadsGuard;

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
    public void ResponseDiscipline_CarriesTheLoadBearingClauses()
    {
        // Issue #2493: every launched runtime sees these three numbered
        // requirements. Pin them so a reword has to be deliberate.
        var guard = ResponseDiscipline;

        guard.ShouldContain("Spring Voyage runtime guard — response discipline");
        guard.ShouldContain("final reply A2A message");
        guard.ShouldContain("sv.progress.report");
        guard.ShouldContain("Silent success is a regression");
        guard.ShouldContain("End Spring Voyage runtime guard — response discipline");
    }

    [Fact]
    public void Compose_ConcurrentThreadsFalse_StillPrependsResponseDiscipline()
    {
        // Issue #2493: ResponseDiscipline is always-on, independent of
        // the concurrent_threads flag.
        var composed = Compose(SampleUserPrompt, concurrentThreads: false);

        composed.ShouldStartWith("## Spring Voyage runtime guard — response discipline");
        composed.ShouldEndWith(SampleUserPrompt);
        composed.ShouldNotContain("concurrent_threads is on");
    }

    [Fact]
    public void Compose_ConcurrentThreadsTrue_PrependsBothGuards_ResponseDisciplineFirst()
    {
        var composed = Compose(SampleUserPrompt, concurrentThreads: true);

        composed.ShouldStartWith("## Spring Voyage runtime guard — response discipline");
        composed.ShouldEndWith(SampleUserPrompt);
        composed.ShouldContain(SampleUserPrompt, Case.Sensitive);
        // Response discipline precedes the concurrent-threads guard
        // (model attention biases toward early context — the universal
        // contract goes first).
        composed.IndexOf("response discipline", StringComparison.Ordinal)
            .ShouldBeLessThan(composed.IndexOf("concurrent_threads is on", StringComparison.Ordinal));
        // Both precede the user's prompt.
        composed.IndexOf("concurrent_threads is on", StringComparison.Ordinal)
            .ShouldBeLessThan(composed.IndexOf(SampleUserPrompt, StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_EmptyPrompt_StillEmitsResponseDiscipline()
    {
        // A sparse / empty prompt still gets the universal guard.
        var composed = Compose(string.Empty, concurrentThreads: false);

        composed.ShouldStartWith("## Spring Voyage runtime guard — response discipline");
        composed.ShouldContain("End Spring Voyage runtime guard — response discipline");
    }
}
