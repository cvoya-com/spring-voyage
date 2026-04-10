// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

/// <summary>
/// Output from the <see cref="CloningLifecycleWorkflow"/>.
/// </summary>
/// <param name="Success">Whether the cloning operation completed successfully.</param>
/// <param name="Error">An error message when the operation fails.</param>
public record CloningOutput(
    bool Success,
    string? Error = null);
