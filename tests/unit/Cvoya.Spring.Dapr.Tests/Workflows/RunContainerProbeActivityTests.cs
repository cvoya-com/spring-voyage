// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RunContainerProbeActivity"/>, including the
/// security-critical credential redaction canary.
/// </summary>
public class RunContainerProbeActivityTests
{
    private const string TestImage = "ghcr.io/cvoya/test:1";
    private const string TestRuntimeId = "test-runtime";
    private const string TestLauncherId = "test-launcher";
    private const string TestModel = "gpt-4o";

    private readonly IRuntimeCatalog _catalog;
    private readonly IAgentRuntimeLauncherRegistry _launcherRegistry;
    private readonly IContainerRuntime _containerRuntime;
    private readonly RunContainerProbeActivity _activity;

    public RunContainerProbeActivityTests()
    {
        _catalog = Substitute.For<IRuntimeCatalog>();
        _launcherRegistry = Substitute.For<IAgentRuntimeLauncherRegistry>();
        _containerRuntime = Substitute.For<IContainerRuntime>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new RunContainerProbeActivity(_catalog, _launcherRegistry, _containerRuntime, loggerFactory);

        // Default: the catalogue has a runtime entry that points at the
        // test launcher id. Tests can override via Substitute.
        var runtime = new Cvoya.Spring.Core.Catalog.AgentRuntime(
            Id: TestRuntimeId,
            DisplayName: "Test Runtime",
            DefaultImage: TestImage,
            Launcher: TestLauncherId,
            ThreadBinding: new ThreadBinding(ThreadBindingKind.EnvVar, EnvVarName: "T_ID"),
            SystemPromptInjection: new SystemPromptInjection(SystemPromptInjectionKind.EnvVar, EnvVarName: "P_ID"),
            ModelProviders: Array.Empty<AgentRuntimeProviderEdge>());
        _catalog.GetAgentRuntime(TestRuntimeId).Returns(runtime);
    }

    private static Func<int, string, string, StepResult> SuccessInterpreter(
        IReadOnlyDictionary<string, string>? extras = null) =>
        (_, _, _) => StepResult.Succeed(extras);

    private static Func<int, string, string, StepResult> FailureInterpreter(
        string code, string message, IReadOnlyDictionary<string, string>? details = null) =>
        (_, _, _) => StepResult.Fail(code, message, details);

    private void RegisterStep(
        ArtefactValidationStep step,
        Func<int, string, string, StepResult> interpreter)
    {
        var launcher = Substitute.For<IAgentRuntimeLauncher>();
        launcher.Kind.Returns(TestLauncherId);
        launcher.GetProbeSteps(Arg.Any<ModelProviderInstallConfig>(), Arg.Any<string>())
            .Returns(new[]
            {
                new ProbeStep(
                    Step: step,
                    Args: new[] { "sh", "-c", "echo test" },
                    Env: new Dictionary<string, string>(StringComparer.Ordinal),
                    Timeout: TimeSpan.FromSeconds(5),
                    InterpretOutput: interpreter),
            });
        _launcherRegistry.Get(TestLauncherId).Returns(launcher);
    }

    private static RunContainerProbeActivityInput Input(
        ArtefactValidationStep step = ArtefactValidationStep.VerifyingTool,
        string credential = "test-credential") =>
        new(TestRuntimeId, step, TestImage, credential, TestModel);

    [Fact]
    public async Task RunAsync_StepSucceeds_ReturnsSuccessAndExtras()
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["models"] = "gpt-4o,gpt-4o-mini",
        };
        RegisterStep(ArtefactValidationStep.ResolvingModel, SuccessInterpreter(extras));
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("c1", 0, "ok", string.Empty));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(
            context, Input(ArtefactValidationStep.ResolvingModel));

        result.Success.ShouldBeTrue();
        result.Failure.ShouldBeNull();
        result.Extras.ShouldNotBeNull();
        result.Extras!["models"].ShouldBe("gpt-4o,gpt-4o-mini");
    }

    [Fact]
    public async Task RunAsync_InterpreterFails_PropagatesCodeAndMessage()
    {
        RegisterStep(
            ArtefactValidationStep.ValidatingCredential,
            FailureInterpreter(
                ArtefactValidationCodes.CredentialInvalid,
                "rejected (HTTP 401)",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["http_status"] = "401" }));
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("c1", 0, "401", string.Empty));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(
            context, Input(ArtefactValidationStep.ValidatingCredential));

        result.Success.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Code.ShouldBe(ArtefactValidationCodes.CredentialInvalid);
        result.Failure.Message.ShouldContain("401");
        result.Failure.Step.ShouldBe(ArtefactValidationStep.ValidatingCredential);
    }

    [Fact]
    public async Task RunAsync_ContainerStartFails_ReturnsImageStartFailed()
    {
        RegisterStep(ArtefactValidationStep.VerifyingTool, SuccessInterpreter());
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("bad entrypoint"));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(ArtefactValidationCodes.ImageStartFailed);
        result.Failure.Message.ShouldContain("bad entrypoint");
    }

    [Fact]
    public async Task RunAsync_ContainerTimesOut_ReturnsProbeTimeout()
    {
        RegisterStep(ArtefactValidationStep.VerifyingTool, SuccessInterpreter());
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("container hung"));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(ArtefactValidationCodes.ProbeTimeout);
    }

    [Fact]
    public async Task RunAsync_InterpreterThrows_ReturnsProbeInternalError()
    {
        RegisterStep(
            ArtefactValidationStep.VerifyingTool,
            (_, _, _) => throw new InvalidOperationException("boom"));
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("c1", 0, "ok", string.Empty));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(ArtefactValidationCodes.ProbeInternalError);
        result.Failure.Message.ShouldContain("boom");
    }

    [Fact]
    public async Task RunAsync_RuntimeNotRegistered_ReturnsProbeInternalError()
    {
        _catalog.GetAgentRuntime(TestRuntimeId).Returns((Cvoya.Spring.Core.Catalog.AgentRuntime?)null);
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(ArtefactValidationCodes.ProbeInternalError);
        result.Failure.Message.ShouldContain(TestRuntimeId);
    }

    [Fact]
    public async Task RunAsync_StepNotDeclared_ReturnsProbeInternalError()
    {
        // Runtime declares only VerifyingTool but we ask for ResolvingModel.
        RegisterStep(ArtefactValidationStep.VerifyingTool, SuccessInterpreter());
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(
            context, Input(ArtefactValidationStep.ResolvingModel));

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(ArtefactValidationCodes.ProbeInternalError);
        result.Failure.Message.ShouldContain("ResolvingModel");
    }

    /// <summary>
    /// Security-critical canary test: if the probe command echoes the
    /// credential through stdout/stderr AND the interpreter forwards that
    /// text into its <see cref="StepResult.Message"/> and
    /// <see cref="StepResult.Details"/>, the activity MUST scrub every
    /// occurrence via <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/>
    /// before the <see cref="RunContainerProbeActivityOutput"/> leaves.
    /// Leaking here means leaking into persisted unit errors, web SSE
    /// events, and CLI output.
    /// </summary>
    [Fact]
    public async Task RunAsync_RedactionCanary_RedactsCredentialEverywhere()
    {
        var canary = $"SPRING_PROBE_CANARY_{Guid.NewGuid():N}";
        // Interpreter deliberately stuffs the canary back into Message +
        // Details so we can verify the activity's belt-and-braces redaction.
        RegisterStep(
            ArtefactValidationStep.ValidatingCredential,
            (_, stdout, stderr) => StepResult.Fail(
                ArtefactValidationCodes.CredentialInvalid,
                $"stdout was '{stdout}', stderr was '{stderr}', raw: {canary}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["leaked"] = canary,
                    ["also_leaked"] = $"prefix-{canary}-suffix",
                }));

        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult(
                "c1",
                1,
                StandardOutput: $"api key {canary} rejected",
                StandardError: $"auth fail {canary}"));

        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(
            context, Input(ArtefactValidationStep.ValidatingCredential, credential: canary));

        // stdout/stderr surfaced on the output record must be scrubbed.
        result.RedactedStdOut.ShouldNotContain(canary);
        result.RedactedStdOut.ShouldContain("***");
        result.RedactedStdErr.ShouldNotContain(canary);
        result.RedactedStdErr.ShouldContain("***");

        // Interpreter shoved the canary into Message + Details; belt-and-
        // braces redaction MUST scrub both.
        result.Failure.ShouldNotBeNull();
        result.Failure!.Message.ShouldNotContain(canary);
        result.Failure.Message.ShouldContain("***");

        result.Failure.Details.ShouldNotBeNull();
        foreach (var value in result.Failure.Details!.Values)
        {
            value.ShouldNotContain(canary);
        }

        // Spot-check the keys the test stuffed.
        result.Failure.Details["leaked"].ShouldBe("***");
        result.Failure.Details["also_leaked"].ShouldBe("prefix-***-suffix");
    }
}
