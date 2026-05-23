// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Test-side helpers that fabricate <see cref="RuntimeOutcome"/> values
/// covering the three terminal cases the dispatch coordinator distinguishes
/// (ADR-0056 §5):
/// <list type="bullet">
///   <item><description><see cref="Success"/> — clean exit AND tool calls observed (RuntimeCompleted terminal).</description></item>
///   <item><description><see cref="Silent"/> — clean exit, zero tool calls (RuntimeCompletedSilent terminal).</description></item>
///   <item><description><see cref="Failure"/> — non-zero exit (RuntimeFailed terminal).</description></item>
/// </list>
/// Keeping these in one place means a future shape change on
/// <see cref="RuntimeOutcome"/> updates every test fixture at once.
/// </summary>
public static class RuntimeOutcomes
{
    /// <summary>
    /// A clean-exit outcome reporting <paramref name="toolCallCount"/>
    /// dispatched tool calls (default 1 — non-zero so the coordinator
    /// emits <c>RuntimeCompleted</c>, not the silent variant).
    /// </summary>
    public static RuntimeOutcome Success(int toolCallCount = 1, string? reasoningTrace = null)
        => new(
            ExitCode: 0,
            Duration: TimeSpan.FromMilliseconds(1),
            ReasoningTrace: reasoningTrace,
            Diagnostics: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RuntimeOutcome.ToolCallCountKey] = toolCallCount,
            });

    /// <summary>
    /// A clean-exit outcome with <c>toolCallCount = 0</c> — the
    /// silent-completion compliance gap (ADR-0056 §5). Surfaces as
    /// <c>RuntimeCompletedSilent</c> on the activity stream.
    /// </summary>
    public static RuntimeOutcome Silent(string? reasoningTrace = null)
        => Success(toolCallCount: 0, reasoningTrace);

    /// <summary>
    /// A failed-exit outcome with the supplied non-zero <paramref name="exitCode"/>.
    /// Surfaces as <c>RuntimeFailed</c> on the activity stream.
    /// </summary>
    public static RuntimeOutcome Failure(int exitCode = 1, int toolCallCount = 0, string? reasoningTrace = null)
        => new(
            ExitCode: exitCode,
            Duration: TimeSpan.FromMilliseconds(1),
            ReasoningTrace: reasoningTrace,
            Diagnostics: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RuntimeOutcome.ToolCallCountKey] = toolCallCount,
            });
}
