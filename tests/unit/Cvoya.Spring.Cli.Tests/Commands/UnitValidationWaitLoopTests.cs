// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Core.Lifecycle;
using System.Collections.Generic;
using System.IO;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// T-08 / #950: behavioural tests for the polling wait loop that backs
/// <c>spring unit create</c> and <c>spring unit revalidate</c>. The loop
/// is decoupled from the Kiota client via delegate seams so we can drive
/// it entirely from in-memory fixtures — no <c>Task.Delay</c>, no HTTP.
/// </summary>
public class ArtefactValidationWaitLoopTests
{
    /// <summary>
    /// No-op delay delegate: the tests rely on this so poll-interval sleeps
    /// never actually block. A wait-loop bug that forgets to honour this
    /// seam would surface as the test run hanging — xUnit's per-test
    /// cancellation token then cuts in, which is also acceptable.
    /// </summary>
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay =
        (_, _) => Task.CompletedTask;

    private static ArtefactValidationSnapshot Validating(string runId = "run-1") =>
        new(
            Status: "Validating",
            ValidationRunId: runId,
            ErrorCode: null,
            ErrorStep: null,
            ErrorMessage: null,
            ErrorDetails: null);

    private static ArtefactValidationSnapshot Stopped() =>
        new(
            Status: "Stopped",
            ValidationRunId: "run-1",
            ErrorCode: null,
            ErrorStep: null,
            ErrorMessage: null,
            ErrorDetails: null);

    private static ArtefactValidationSnapshot Draft() =>
        new(
            Status: "Draft",
            ValidationRunId: null,
            ErrorCode: null,
            ErrorStep: null,
            ErrorMessage: null,
            ErrorDetails: null);

    private static ArtefactValidationSnapshot Error(
        string code,
        string step,
        string message = "boom",
        IReadOnlyDictionary<string, string>? details = null) =>
        new(
            Status: "Error",
            ValidationRunId: "run-1",
            ErrorCode: code,
            ErrorStep: step,
            ErrorMessage: message,
            ErrorDetails: details);

    private static Func<CancellationToken, Task<ArtefactValidationSnapshot>> FetchSequence(
        params ArtefactValidationSnapshot[] snapshots)
    {
        var queue = new Queue<ArtefactValidationSnapshot>(snapshots);
        return _ => Task.FromResult(
            queue.Count > 0
                ? queue.Dequeue()
                : snapshots[^1]);
    }

    [Fact]
    public async Task RunAsync_ValidatingThenStopped_PrintsSuccessAndReturnsZero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // Two Validating polls (after the initial Validating snapshot) then
        // Stopped — asserts that the loop waits through progress polls and
        // only prints terminal success once a terminal state is observed.
        var fetch = FetchSequence(Validating(), Validating(), Stopped());

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Validating(),
            fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(ArtefactValidationExitCodes.Success);
        stdout.ToString().ShouldContain("Validating...");
        stdout.ToString().ShouldContain("Validation passed");
        stdout.ToString().ShouldContain("eng-team");
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_ValidatingProgress_SuppressesDuplicateTransitionLines()
    {
        // The coarse "Validating..." indicator should only print on state
        // transitions — spamming it once per second would make the terminal
        // unreadable on longer validations. Three Validating polls should
        // produce exactly one line.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fetch = FetchSequence(Validating(), Validating(), Validating(), Stopped());

        await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Validating(),
            fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        var validatingLines = stdout.ToString()
            .Split('\n')
            .Count(line => line.Contains("Validating...", StringComparison.Ordinal));
        validatingLines.ShouldBe(1);
    }

    [Theory]
    [InlineData(ArtefactValidationCodes.CredentialInvalid, 23)]
    [InlineData(ArtefactValidationCodes.ModelNotFound, 25)]
    [InlineData(ArtefactValidationCodes.ProbeTimeout, 26)]
    [InlineData(ArtefactValidationCodes.ImagePullFailed, 20)]
    [InlineData(ArtefactValidationCodes.ImageStartFailed, 21)]
    [InlineData(ArtefactValidationCodes.ToolMissing, 22)]
    [InlineData(ArtefactValidationCodes.CredentialFormatRejected, 24)]
    [InlineData(ArtefactValidationCodes.ProbeInternalError, 27)]
    public async Task RunAsync_ValidatingThenError_MapsCodeToExitAndPrintsStderr(
        string code,
        int expectedExit)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fetch = FetchSequence(Validating(), Error(code, step: "ValidatingCredential"));

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Validating(),
            fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(expectedExit);
        var err = stderr.ToString();
        err.ShouldContain("Validation failed");
        err.ShouldContain(code);
        // Step is rendered verbatim from the snapshot — proves the block
        // isn't dropping fields when the code path changes.
        err.ShouldContain("ValidatingCredential");
    }

    [Fact]
    public async Task RunAsync_ErrorWithDetails_RendersKeyValuePairs()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var details = new Dictionary<string, string>
        {
            ["lastStderr"] = "fatal: registry returned 404",
            ["exitCode"] = "125",
        };
        var fetch = FetchSequence(
            Error(ArtefactValidationCodes.ImagePullFailed, step: "PullingImage", details: details));

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Validating(),
            fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(20);
        var err = stderr.ToString();
        err.ShouldContain("details:");
        err.ShouldContain("lastStderr: fatal: registry returned 404");
        err.ShouldContain("exitCode: 125");
    }

    [Fact]
    public async Task RunAsync_AlreadyTerminalAtStart_NoPollsMade()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // If the initial snapshot is already Stopped, the loop should
        // never call the fetcher — poll-count pressure matters on
        // scripted runs that hammer create repeatedly.
        var callCount = 0;
        Task<ArtefactValidationSnapshot> Fetch(CancellationToken _)
        {
            callCount++;
            return Task.FromResult(Stopped());
        }

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Stopped(),
            Fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(ArtefactValidationExitCodes.Success);
        callCount.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_AlreadyDraftAtStart_ReturnsSuccessAndNoPolls()
    {
        // #1025 — a minimal unit (no model / no provider) lands in Draft; the
        // server never schedules a validation workflow so the wait loop must
        // treat Draft as terminal, not poll forever.
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var callCount = 0;
        Task<ArtefactValidationSnapshot> Fetch(CancellationToken _)
        {
            callCount++;
            return Task.FromResult(Draft());
        }

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Draft(),
            Fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(ArtefactValidationExitCodes.Success);
        callCount.ShouldBe(0);
        stdout.ToString().ShouldContain("Draft");
        stdout.ToString().ShouldContain("Add a model");
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_ValidatingThenDraft_ReturnsSuccessAndStopsPolling()
    {
        // Covers the less-common path where the server returns Validating
        // on the initial POST but the unit settles back into Draft (e.g. a
        // model reference gets cleared). The loop must still terminate.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fetch = FetchSequence(Validating(), Draft());

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Validating(),
            fetch,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(ArtefactValidationExitCodes.Success);
        stdout.ToString().ShouldContain("Draft");
        stdout.ToString().ShouldContain("Add a model");
    }

    [Fact]
    public async Task RunAsync_CancelledMidWait_ReturnsUnknownErrorAndWritesStderr()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var cts = new CancellationTokenSource();

        async Task<ArtefactValidationSnapshot> Fetch(CancellationToken token)
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            return Validating();
        }

        var exit = await ArtefactValidationWaitLoop.RunAsync(
            "eng-team",
            Validating(),
            Fetch,
            stdout,
            stderr,
            cts.Token,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: NoDelay);

        exit.ShouldBe(ArtefactValidationExitCodes.UnknownError);
        stderr.ToString().ShouldContain("cancelled", Case.Insensitive);
    }
}
