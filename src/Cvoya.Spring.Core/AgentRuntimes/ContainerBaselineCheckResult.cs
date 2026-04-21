// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Outcome of a container-baseline probe. Reports whether a subject's
/// required tooling (CLI binaries, network reachability, etc.) is present
/// in the target container image.
/// </summary>
/// <remarks>
/// Used today by
/// <see cref="Cvoya.Spring.Connectors.IConnectorType.VerifyContainerBaselineAsync(System.Threading.CancellationToken)"/>.
/// Agent runtimes no longer expose a separate baseline probe; their
/// in-container tool verification runs as the
/// <see cref="Cvoya.Spring.Core.Units.UnitValidationStep.VerifyingTool"/>
/// step of the <c>UnitValidationWorkflow</c> probe plan returned by
/// <see cref="IAgentRuntime.GetProbeSteps(AgentRuntimeInstallConfig, string)"/>.
/// </remarks>
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