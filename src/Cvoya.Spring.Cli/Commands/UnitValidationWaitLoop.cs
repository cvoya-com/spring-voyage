// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Snapshot of the server-observable unit-validation state polled by the
/// CLI wait loop. Decoupled from the Kiota generated model so the loop
/// can be unit-tested against stubs without standing up an API.
///
/// T-08 / #950 — the CLI polls this shape from <c>GET /api/v1/units/{name}</c>
/// once per second and prints progress / terminal state transitions.
/// </summary>
/// <param name="Status">
/// The unit status string (e.g. <c>Validating</c>, <c>Stopped</c>,
/// <c>Error</c>, <c>Draft</c>). Kept as a string so test fixtures
/// don't need to build Kiota enums.
/// </param>
/// <param name="ValidationRunId">
/// <c>LastValidationRunId</c> from the server — the workflow instance id
/// that produced the current state. Null when the workflow has not yet
/// been scheduled.
/// </param>
/// <param name="ErrorCode">
/// <c>LastValidationError.Code</c> when present (see
/// <c>UnitValidationCodes</c> for the stable set). Null when the unit has
/// no recorded error (e.g. still <c>Validating</c>, or <c>Stopped</c>).
/// </param>
/// <param name="ErrorStep">
/// <c>LastValidationError.Step</c> when present (one of the
/// <c>UnitValidationStep</c> enum values, as a string).
/// </param>
/// <param name="ErrorMessage">
/// <c>LastValidationError.Message</c> when present — the human-readable
/// explanation emitted by the workflow.
/// </param>
/// <param name="ErrorDetails">
/// <c>LastValidationError.Details</c> when present — opaque
/// key/value pairs the workflow attached to disambiguate the failure
/// (e.g. probe exit code, last stderr line). Printed as an indented
/// block below the main error fields.
/// </param>
public readonly record struct UnitValidationSnapshot(
    string Status,
    string? ValidationRunId,
    string? ErrorCode,
    string? ErrorStep,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string>? ErrorDetails);

/// <summary>
/// Polling-based wait loop for the CLI's <c>spring unit create</c> and
/// <c>spring unit revalidate</c> verbs. Transitions the CLI from "create /
/// revalidate accepted" to a terminal outcome by polling <c>GET
/// /api/v1/units/{name}</c> every <c>pollInterval</c> until the status
/// is one of <c>Draft</c>, <c>Stopped</c>, or <c>Error</c>.
///
/// T-08 / #950 — polling is the ratified UX (not SSE); SSE is reserved for
/// the web portal which can render the richer per-step channel. The CLI's
/// progress surface is therefore coarse-grained: a single "Validating…"
/// indicator until terminal, then either a success line on stdout or a
/// structured error block on stderr.
///
/// #1025 — <c>Draft</c> is terminal alongside <c>Stopped</c> / <c>Error</c>.
/// A minimal unit (no model, no provider) has nothing to validate and never
/// leaves <c>Draft</c>, so treating it as in-flight would poll forever. The
/// gate is the server-side workflow: if it never ran, there is nothing to
/// wait for.
/// </summary>
public static class UnitValidationWaitLoop
{
    /// <summary>Default poll interval (1 second) — see T-00 design inputs on #942.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs the wait loop against <paramref name="fetchSnapshot"/> until the
    /// unit reaches <c>Stopped</c> or <c>Error</c>, the caller cancels the
    /// token, or an exception bubbles up.
    ///
    /// Progress UX (T-00): a single "Validating…" transition line is
    /// emitted the first time the unit is observed in <c>Validating</c>;
    /// subsequent polls that observe the same state suppress re-emission so
    /// the terminal doesn't spam once per second. On terminal state, emits
    /// either the success line or the structured-error block and returns
    /// the corresponding exit code via <see cref="UnitValidationExitCodes"/>.
    /// </summary>
    /// <param name="unitName">
    /// The unit name; rendered in output so the caller can identify which
    /// unit is being waited on when multiple CLIs share a terminal.
    /// </param>
    /// <param name="initialSnapshot">
    /// The snapshot the caller already has in hand (from the create /
    /// revalidate POST response). When its status is already terminal we
    /// skip polling and emit the terminal line immediately.
    /// </param>
    /// <param name="fetchSnapshot">
    /// Fetches a fresh snapshot. Injected as a delegate so the loop is
    /// unit-testable without an HTTP client — production wires it to
    /// <c>SpringApiClient.GetUnitAsync</c>.
    /// </param>
    /// <param name="stdout">Writer for success output.</param>
    /// <param name="stderr">Writer for error output.</param>
    /// <param name="ct">Propagates operator Ctrl+C through the poll loop.</param>
    /// <param name="pollInterval">
    /// Poll interval; defaults to <see cref="DefaultPollInterval"/>.
    /// </param>
    /// <param name="delay">
    /// Test seam for the inter-poll sleep. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// so production calls sleep for real; tests inject a no-op delegate
    /// to keep the run instantaneous.
    /// </param>
    /// <returns>
    /// A <see cref="UnitValidationExitCodes"/> value: <c>0</c> on success,
    /// <c>1</c> on unknown / cancellation, or one of the <c>20..27</c>
    /// codes on a recognised validation failure.
    /// </returns>
    public static async Task<int> RunAsync(
        string unitName,
        UnitValidationSnapshot initialSnapshot,
        Func<CancellationToken, Task<UnitValidationSnapshot>> fetchSnapshot,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);
        ArgumentNullException.ThrowIfNull(fetchSnapshot);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var interval = pollInterval ?? DefaultPollInterval;
        var sleep = delay ?? Task.Delay;

        var snapshot = initialSnapshot;
        // Track the last "observable" state so we only emit a progress line
        // on transitions. The first snapshot always emits a line — there's
        // no prior state to compare against.
        string? lastObservedStatus = null;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!string.Equals(snapshot.Status, lastObservedStatus, StringComparison.Ordinal))
                {
                    EmitProgressLine(snapshot, stdout);
                    lastObservedStatus = snapshot.Status;
                }

                if (IsTerminal(snapshot.Status))
                {
                    return await FinaliseAsync(unitName, snapshot, stdout, stderr);
                }

                await sleep(interval, ct).ConfigureAwait(false);
                snapshot = await fetchSnapshot(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync(
                $"Wait cancelled for unit '{unitName}'. The validation workflow continues on the server; " +
                "re-check with 'spring unit get <name>'.").ConfigureAwait(false);
            return UnitValidationExitCodes.UnknownError;
        }
    }

    private static bool IsTerminal(string? status) =>
        string.Equals(status, "Stopped", StringComparison.Ordinal)
        || string.Equals(status, "Error", StringComparison.Ordinal)
        || string.Equals(status, "Draft", StringComparison.Ordinal);

    private static void EmitProgressLine(UnitValidationSnapshot snapshot, TextWriter stdout)
    {
        if (string.Equals(snapshot.Status, "Validating", StringComparison.Ordinal))
        {
            // T-00 accepted the "single coarse indicator" UX for polling —
            // no per-step lines inferred from side-channel heuristics. If
            // finer-grained progress is desired, the web portal's SSE
            // surface is the right channel (see v2.1 follow-up on the PR).
            var runId = string.IsNullOrWhiteSpace(snapshot.ValidationRunId)
                ? "pending"
                : snapshot.ValidationRunId;
            stdout.WriteLine($"Validating... (run id: {runId})");
        }
        // Stopped/Error/Draft are handled by FinaliseAsync (emits the
        // terminal line); every other state (Starting/Running/Stopping) is
        // a pass-through observation — the loop prints the raw state so
        // operators watching the terminal can see what the server is
        // reporting.
        else if (!IsTerminal(snapshot.Status))
        {
            stdout.WriteLine($"Status: {snapshot.Status}");
        }
    }

    private static async Task<int> FinaliseAsync(
        string unitName,
        UnitValidationSnapshot snapshot,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (string.Equals(snapshot.Status, "Stopped", StringComparison.Ordinal))
        {
            await stdout.WriteLineAsync(
                $"Validation passed. Unit '{unitName}' is ready to start.").ConfigureAwait(false);
            return UnitValidationExitCodes.Success;
        }

        if (string.Equals(snapshot.Status, "Draft", StringComparison.Ordinal))
        {
            // #1025 — a minimal unit (no model / no provider) lands in Draft
            // and the server never schedules a workflow, so there's nothing
            // to wait for. The unit was accepted; tell the operator what
            // they're looking at and exit 0. The nudge points at the next
            // concrete action rather than leaving them to guess.
            await stdout.WriteLineAsync(
                $"Unit '{unitName}' is in Draft; validation has not started. " +
                "Add a model to validate (e.g. 'spring unit update' with --model).").ConfigureAwait(false);
            return UnitValidationExitCodes.Success;
        }

        // Error path — print the structured error block on stderr.
        await stderr.WriteLineAsync("Validation failed:").ConfigureAwait(false);
        await stderr.WriteLineAsync($"  step:    {snapshot.ErrorStep ?? "(unknown)"}").ConfigureAwait(false);
        await stderr.WriteLineAsync($"  code:    {snapshot.ErrorCode ?? "(unknown)"}").ConfigureAwait(false);
        await stderr.WriteLineAsync($"  message: {snapshot.ErrorMessage ?? "(no message)"}").ConfigureAwait(false);
        await stderr.WriteLineAsync($"  runId:   {snapshot.ValidationRunId ?? "(unknown)"}").ConfigureAwait(false);
        if (snapshot.ErrorDetails is { Count: > 0 } details)
        {
            await stderr.WriteLineAsync("  details:").ConfigureAwait(false);
            foreach (var (key, value) in details)
            {
                await stderr.WriteLineAsync($"    {key}: {value}").ConfigureAwait(false);
            }
        }
        return UnitValidationExitCodes.ForCode(snapshot.ErrorCode);
    }
}