// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Represents token usage statistics from an AI provider response.
/// </summary>
/// <param name="InputTokens">The number of input tokens consumed.</param>
/// <param name="OutputTokens">The number of output tokens generated.</param>
/// <param name="Model">The model identifier used for the completion.</param>
/// <param name="Cost">The estimated cost in USD for this completion, if available.</param>
/// <param name="Duration">The wall-clock duration of the completion request.</param>
public record UsageStats(
    int InputTokens,
    int OutputTokens,
    string? Model = null,
    decimal? Cost = null,
    TimeSpan? Duration = null);
