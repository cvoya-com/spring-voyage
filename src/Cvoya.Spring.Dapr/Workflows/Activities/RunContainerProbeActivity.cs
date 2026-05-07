// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Units;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs one probe step inside an already-pulled container image, redacts the
/// credential from the output, feeds the triple through the runtime's
/// <see cref="ProbeStep.InterpretOutput"/>, and returns a structured
/// <see cref="RunContainerProbeActivityOutput"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per T-04's refinement of the T-03 contract, interpretation happens inside
/// this activity rather than in the workflow body — the delegate lives in
/// the runtime plugin and resolving it from a
/// <see cref="IAgentRuntimeRegistry"/> singleton only works in a process
/// with DI. Keeping that call here lets the workflow stay deterministic and
/// delegate-free.
/// </para>
/// <para>
/// Redaction runs twice by design: once on the raw stdout/stderr before
/// the interpreter sees them (so a sloppy interpreter can't echo the
/// credential into <see cref="StepResult.Message"/>), and again on the
/// derived <c>Message</c> / <c>Details</c> values just before the activity
/// returns.
/// </para>
/// </remarks>
public class RunContainerProbeActivity(
    IAgentRuntimeRegistry runtimeRegistry,
    IContainerRuntime containerRuntime,
    ILoggerFactory loggerFactory)
    : WorkflowActivity<RunContainerProbeActivityInput, RunContainerProbeActivityOutput>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RunContainerProbeActivity>();

    /// <inheritdoc />
    public override async Task<RunContainerProbeActivityOutput> RunAsync(
        WorkflowActivityContext context, RunContainerProbeActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var runtime = runtimeRegistry.Get(input.RuntimeId);
        if (runtime is null)
        {
            _logger.LogWarning(
                "No agent runtime registered with id '{RuntimeId}' for probe step {Step}.",
                input.RuntimeId, input.Step);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeInternalError,
                $"No agent runtime is registered with id '{input.RuntimeId}'.",
                details: null,
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }

        // Build a minimal install config that mirrors what T-05 will pass in
        // when it starts the workflow from UnitActor: the default model is
        // the requested model (drives ResolvingModel), BaseUrl is left null
        // so runtimes use their seed default.
        var config = new AgentRuntimeInstallConfig(
            Models: Array.Empty<string>(),
            DefaultModel: input.RequestedModel,
            BaseUrl: null);

        IReadOnlyList<ProbeStep> steps;
        try
        {
            steps = runtime.GetProbeSteps(config, input.Credential ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Runtime {RuntimeId} threw from GetProbeSteps for step {Step}.",
                input.RuntimeId, input.Step);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeInternalError,
                $"Runtime '{input.RuntimeId}' failed to produce probe steps: {ex.Message}",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exception_type"] = ex.GetType().Name,
                },
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }

        var step = steps.FirstOrDefault(s => s.Step == input.Step);
        if (step is null)
        {
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeInternalError,
                $"Runtime '{input.RuntimeId}' did not declare a probe step for '{input.Step}'.",
                details: null,
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }

        // #1686: override the image's ENTRYPOINT so the probe runs the
        // declared tool (e.g. `claude --version`) directly. The OSS agent
        // images inherit a long-running A2A bridge as their ENTRYPOINT
        // (per Dockerfile.agent.claude-code) — without this override the
        // bridge swallows the CMD and the probe deadlocks until the per-
        // step timeout fires. Convention: the first entry of step.Args is
        // the binary; the rest are its arguments.
        var probeArgs = step.Args ?? Array.Empty<string>();
        var entrypoint = probeArgs.Count > 0 ? probeArgs[0] : null;
        var commandArgs = probeArgs.Count > 1
            ? probeArgs.Skip(1).ToArray()
            : Array.Empty<string>();

        var containerConfig = new ContainerConfig(
            Image: input.Image,
            Command: commandArgs,
            EnvironmentVariables: step.Env,
            Timeout: step.Timeout,
            Entrypoint: entrypoint);

        ContainerResult containerResult;
        try
        {
            // Linked CTS enforces the step timeout inside this activity so a
            // runtime that hangs inside RunAsync surfaces as ProbeTimeout
            // rather than stalling the workflow until Dapr's activity-level
            // retry semantics kick in.
            using var timeoutCts = new CancellationTokenSource(step.Timeout);
            containerResult = await containerRuntime.RunAsync(containerConfig, timeoutCts.Token);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "Probe step {Step} timed out for runtime {RuntimeId}.",
                input.Step, input.RuntimeId);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeTimeout,
                $"Probe step '{input.Step}' exceeded the configured timeout of {step.Timeout}.",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["timeout"] = step.Timeout.ToString(),
                },
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Probe step {Step} was cancelled for runtime {RuntimeId}.",
                input.Step, input.RuntimeId);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeTimeout,
                $"Probe step '{input.Step}' was cancelled after {step.Timeout}.",
                details: null,
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }
        catch (InvalidOperationException ex)
        {
            // Container runtimes surface start failures (bad entrypoint,
            // immediate crash) as InvalidOperationException today. That's
            // distinct from a container that ran and exited non-zero — the
            // latter comes back through containerResult below and is the
            // interpreter's job to classify.
            _logger.LogWarning(
                ex,
                "Container failed to start for probe step {Step}, runtime {RuntimeId}.",
                input.Step, input.RuntimeId);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ImageStartFailed,
                $"Container failed to start for probe step '{input.Step}': {ex.Message}",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exception_type"] = ex.GetType().Name,
                },
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Probe step {Step} for runtime {RuntimeId} threw {ExceptionType}.",
                input.Step, input.RuntimeId, ex.GetType().Name);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeInternalError,
                $"Probe step '{input.Step}' threw {ex.GetType().Name}: {ex.Message}",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exception_type"] = ex.GetType().Name,
                },
                redactedStdOut: string.Empty,
                redactedStdErr: string.Empty);
        }

        // Redact BEFORE the interpreter runs so an interpreter that echoes
        // input into its Message or Details can't leak the credential.
        var credential = input.Credential ?? string.Empty;
        var redactedStdOut = CredentialRedactor.Redact(
            containerResult.StandardOutput ?? string.Empty, credential);
        var redactedStdErr = CredentialRedactor.Redact(
            containerResult.StandardError ?? string.Empty, credential);

        StepResult result;
        try
        {
            result = step.InterpretOutput(
                containerResult.ExitCode, redactedStdOut, redactedStdErr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Interpreter for runtime {RuntimeId} step {Step} threw.",
                input.RuntimeId, input.Step);
            return FailureOutput(
                input,
                input.Step,
                UnitValidationCodes.ProbeInternalError,
                $"Runtime '{input.RuntimeId}' interpreter threw {ex.GetType().Name}: {ex.Message}",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exception_type"] = ex.GetType().Name,
                },
                redactedStdOut: redactedStdOut,
                redactedStdErr: redactedStdErr);
        }

        if (result.Outcome == StepOutcome.Succeeded)
        {
            return new RunContainerProbeActivityOutput(
                Success: true,
                Failure: null,
                Extras: RedactValues(result.Extras, credential),
                RedactedStdOut: redactedStdOut,
                RedactedStdErr: redactedStdErr);
        }

        // Interpreter-reported failure: pass through its Code/Message/Details,
        // belt-and-braces redact the Message + Details values.
        var failureCode = result.Code ?? UnitValidationCodes.ProbeInternalError;
        var failureMessage = CredentialRedactor.Redact(result.Message ?? string.Empty, credential);
        var failureDetails = RedactValues(result.Details, credential);

        return FailureOutput(
            input,
            input.Step,
            failureCode,
            failureMessage,
            failureDetails,
            redactedStdOut,
            redactedStdErr);
    }

    private static RunContainerProbeActivityOutput FailureOutput(
        RunContainerProbeActivityInput input,
        UnitValidationStep step,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details,
        string redactedStdOut,
        string redactedStdErr)
    {
        var credential = input.Credential ?? string.Empty;
        var redactedMessage = CredentialRedactor.Redact(message, credential);
        var redactedDetails = RedactValues(details, credential);

        return new RunContainerProbeActivityOutput(
            Success: false,
            Failure: new UnitValidationError(step, code, redactedMessage, redactedDetails),
            Extras: null,
            RedactedStdOut: redactedStdOut,
            RedactedStdErr: redactedStdErr);
    }

    private static IReadOnlyDictionary<string, string>? RedactValues(
        IReadOnlyDictionary<string, string>? source, string credential)
    {
        if (source is null || source.Count == 0)
        {
            return source;
        }

        if (string.IsNullOrEmpty(credential))
        {
            return source;
        }

        var redacted = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            redacted[key] = CredentialRedactor.Redact(value ?? string.Empty, credential);
        }
        return redacted;
    }
}