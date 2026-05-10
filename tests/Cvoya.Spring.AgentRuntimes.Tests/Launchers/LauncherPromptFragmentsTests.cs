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
    public void Compose_ConcurrentThreadsFalse_ReturnsUserPromptVerbatim()
    {
        // false default is safe — no guard emitted. The user's prompt
        // body is the entire returned string.
        var composed = Compose(SampleUserPrompt, concurrentThreads: false);

        composed.ShouldBe(SampleUserPrompt);
        composed.ShouldNotContain("Spring Voyage runtime guard");
    }

    [Fact]
    public void Compose_ConcurrentThreadsTrue_PrependsGuardBeforeUserPrompt()
    {
        var composed = Compose(SampleUserPrompt, concurrentThreads: true);

        composed.ShouldStartWith("## Spring Voyage runtime guard");
        composed.ShouldEndWith(SampleUserPrompt);
        composed.ShouldContain(SampleUserPrompt, Case.Sensitive);

        // Composition order matters — the guard MUST come first so the
        // model treats it as platform-level instructions ahead of the
        // user's narrative (model attention biases toward early context).
        composed.IndexOf("Spring Voyage runtime guard", StringComparison.Ordinal)
            .ShouldBeLessThan(composed.IndexOf(SampleUserPrompt, StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_ConcurrentThreadsTrue_EmptyPrompt_StillEmitsGuard()
    {
        // An empty user prompt is unusual but not invalid — the guard
        // still emits so the model sees the constraints regardless.
        var composed = Compose(string.Empty, concurrentThreads: true);

        composed.ShouldStartWith("## Spring Voyage runtime guard");
        composed.ShouldContain("End Spring Voyage runtime guard");
    }
}
