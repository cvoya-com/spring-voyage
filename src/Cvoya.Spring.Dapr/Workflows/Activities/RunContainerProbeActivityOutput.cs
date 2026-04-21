// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

/// <summary>
/// Output of the <c>RunContainerProbeActivity</c> — the raw in-container
/// exec triple with stdout / stderr already redacted.
/// </summary>
/// <remarks>
/// <para>
/// The workflow feeds this triple into the originating
/// <see cref="Cvoya.Spring.Core.AgentRuntimes.ProbeStep.InterpretOutput"/>
/// delegate to derive a
/// <see cref="Cvoya.Spring.Core.AgentRuntimes.StepResult"/>, then maps any
/// failure onto <see cref="Cvoya.Spring.Core.Units.UnitValidationError"/>
/// with the originating step's
/// <see cref="Cvoya.Spring.Core.Units.UnitValidationStep"/>. This split —
/// value-only activity output + in-process interpreter — is the T-03
/// serialization contract: activity records carry no delegates, so they
/// round-trip through the Dapr Workflow JSON serializer without
/// special-casing.
/// </para>
/// <para>
/// Both <see cref="StdOut"/> and <see cref="StdErr"/> MUST have been passed
/// through <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/>
/// inside the activity BEFORE this record is returned. The interpreter
/// delegate and every downstream consumer can treat the strings as
/// already-redacted.
/// </para>
/// </remarks>
/// <param name="ExitCode">The container process' exit code. <c>0</c> conventionally means success; interpreters MUST still inspect <see cref="StdOut"/> for provider-level errors (HTTP 401 bodies, error envelopes, etc.).</param>
/// <param name="StdOut">Redacted standard output captured from the container process.</param>
/// <param name="StdErr">Redacted standard error captured from the container process.</param>
public record RunContainerProbeActivityOutput(
    int ExitCode,
    string StdOut,
    string StdErr);