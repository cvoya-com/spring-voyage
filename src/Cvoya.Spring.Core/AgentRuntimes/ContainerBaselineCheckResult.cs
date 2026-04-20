// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Outcome of
/// <see cref="IAgentRuntime.VerifyContainerBaselineAsync(System.Threading.CancellationToken)"/>.
/// Reports whether the runtime's required tooling (CLI binaries, network
/// reachability, etc.) is present in the current process/container.
/// </summary>
/// <param name="Passed">
/// <c>true</c> when every baseline check succeeded. <c>false</c> when at
/// least one check failed — see <paramref name="Errors"/> for details.
/// </param>
/// <param name="Errors">
/// One human-readable entry per failed check. Empty when
/// <paramref name="Passed"/> is <c>true</c>.
/// </param>
public sealed record ContainerBaselineCheckResult(
    bool Passed,
    IReadOnlyList<string> Errors);