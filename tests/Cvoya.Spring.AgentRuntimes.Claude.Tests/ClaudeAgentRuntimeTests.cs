// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Claude.Tests;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Behaviour tests for <see cref="ClaudeAgentRuntime"/> after the T-03
/// probe-contract migration (#945). Exercises the returned
/// <see cref="ProbeStep"/> plan + each step's InterpretOutput delegate with
/// small string fixtures matching what the in-container CLI emits.
/// </summary>
public class ClaudeAgentRuntimeTests
{
    [Fact]
    public void Identity_MatchesContract()
    {
        var runtime = CreateRuntime(out _);

        runtime.Id.ShouldBe("claude");
        runtime.ToolKind.ShouldBe("claude-code-cli");
        runtime.DisplayName.ShouldBe("Claude (Claude Code CLI + Anthropic API)");
        runtime.CredentialSchema.Kind.ShouldBe(AgentRuntimeCredentialKind.ApiKey);
        runtime.CredentialSchema.DisplayHint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DefaultModels_LoadFromEmbeddedSeed()
    {
        var runtime = CreateRuntime(out _);

        runtime.DefaultModels.Count.ShouldBeGreaterThan(0);
        var ids = runtime.DefaultModels.Select(m => m.Id).ToArray();
        ids.ShouldContain("claude-sonnet-4-20250514");
        ids.ShouldContain("claude-opus-4-20250514");
        ids.ShouldContain("claude-haiku-4-20250514");
        runtime.DefaultBaseUrl.ShouldBe("https://api.anthropic.com");
    }

    // --- GetProbeSteps plan shape ---

    [Fact]
    public void GetProbeSteps_ReturnsToolCredentialModel_InOrder_WithoutPullingImage()
    {
        var runtime = CreateRuntime(out _);
        var config = StandardConfig();

        var steps = runtime.GetProbeSteps(config, credential: "sk-ant-api03-example");

        steps.Count.ShouldBe(3);
        steps[0].Step.ShouldBe(UnitValidationStep.VerifyingTool);
        steps[1].Step.ShouldBe(UnitValidationStep.ValidatingCredential);
        steps[2].Step.ShouldBe(UnitValidationStep.ResolvingModel);
        // PullingImage is the dispatcher's job and must not appear.
        steps.Select(s => s.Step).ShouldNotContain(UnitValidationStep.PullingImage);
    }

    [Fact]
    public void GetProbeSteps_ApiKey_PopulatesAnthropicApiKeyEnvVar()
    {
        var runtime = CreateRuntime(out _);
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-ant-api03-example");

        var credentialStep = steps.Single(s => s.Step == UnitValidationStep.ValidatingCredential);
        credentialStep.Env.ShouldContainKeyAndValue("ANTHROPIC_API_KEY", "sk-ant-api03-example");
        credentialStep.Env.ShouldNotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void GetProbeSteps_OAuthToken_PopulatesClaudeCodeOAuthTokenEnvVar()
    {
        var runtime = CreateRuntime(out _);
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-ant-oat01-example");

        var credentialStep = steps.Single(s => s.Step == UnitValidationStep.ValidatingCredential);
        credentialStep.Env.ShouldContainKeyAndValue("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-example");
        credentialStep.Env.ShouldNotContainKey("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void GetProbeSteps_AllTimeouts_AreBounded()
    {
        var runtime = CreateRuntime(out _);
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-ant-api03-example");

        foreach (var step in steps)
        {
            step.Timeout.ShouldBeGreaterThan(TimeSpan.Zero);
            step.Timeout.ShouldBeLessThan(TimeSpan.FromMinutes(5));
        }
    }

    // --- VerifyingTool interpretation ---

    [Fact]
    public void InterpretVerifyTool_ExitZero_Succeeds()
    {
        var step = GetStep(UnitValidationStep.VerifyingTool);

        var result = step.InterpretOutput(0, "1.0.0", string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretVerifyTool_NonZeroExit_MapsToToolMissing()
    {
        var step = GetStep(UnitValidationStep.VerifyingTool);

        var result = step.InterpretOutput(127, string.Empty, "claude: command not found");

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ToolMissing);
        result.Message.ShouldNotBeNull().ShouldContain("127");
    }

    // --- ValidatingCredential interpretation ---

    [Fact]
    public void InterpretValidateCredential_HappyPath_Succeeds()
    {
        var step = GetStep(UnitValidationStep.ValidatingCredential);
        var stdout = JsonSerializer.Serialize(new { type = "result", is_error = false, result = "OK" });

        var result = step.InterpretOutput(0, stdout, string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretValidateCredential_Api401_MapsToCredentialInvalid()
    {
        var step = GetStep(UnitValidationStep.ValidatingCredential);
        var stdout = JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = true,
            api_error_status = 401,
            result = "Unauthorized: invalid x-api-key",
        });

        var result = step.InterpretOutput(0, stdout, string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);
        result.Details!["http_status"].ShouldBe("401");
    }

    [Fact]
    public void InterpretValidateCredential_Api400_MapsToCredentialFormatRejected()
    {
        var step = GetStep(UnitValidationStep.ValidatingCredential);
        var stdout = JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = true,
            api_error_status = 400,
            result = "invalid request",
        });

        var result = step.InterpretOutput(0, stdout, string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialFormatRejected);
    }

    [Fact]
    public void InterpretValidateCredential_NonJsonStdout_MapsToProbeInternalError()
    {
        var step = GetStep(UnitValidationStep.ValidatingCredential);

        var result = step.InterpretOutput(0, "   ", "some stderr noise");

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ProbeInternalError);
    }

    [Fact]
    public void InterpretValidateCredential_Malformed_MapsToCredentialFormatRejectedFor422()
    {
        var step = GetStep(UnitValidationStep.ValidatingCredential);
        var stdout = JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = true,
            api_error_status = 422,
            result = "bad credential shape",
        });

        var result = step.InterpretOutput(0, stdout, string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialFormatRejected);
    }

    // --- ResolvingModel interpretation ---

    [Fact]
    public void InterpretResolveModel_HappyPath_SucceedsAndEmitsModelExtra()
    {
        var runtime = CreateRuntime(out _);
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-ant-api03-example");
        var step = steps.Single(s => s.Step == UnitValidationStep.ResolvingModel);
        var stdout = JsonSerializer.Serialize(new { type = "result", is_error = false, result = "OK" });

        var result = step.InterpretOutput(0, stdout, string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Succeeded);
        result.Extras.ShouldNotBeNull();
        result.Extras!["model"].ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public void InterpretResolveModel_Error_MapsToModelNotFound()
    {
        var runtime = CreateRuntime(out _);
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-ant-api03-example");
        var step = steps.Single(s => s.Step == UnitValidationStep.ResolvingModel);
        var stdout = JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = true,
            api_error_status = 404,
            result = "model not found: claude-ghost",
        });

        var result = step.InterpretOutput(0, stdout, string.Empty);

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ModelNotFound);
    }

    // --- FetchLiveModelsAsync (keeps its existing REST shape) ---

    [Fact]
    public async Task FetchLiveModelsAsync_EmptyCredential_ReturnsInvalidCredential()
    {
        var runtime = CreateRuntime(out _);

        var result = await runtime.FetchLiveModelsAsync("   ", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.InvalidCredential);
    }

    [Fact]
    public async Task FetchLiveModelsAsync_OAuthToken_ReturnsUnsupported()
    {
        var runtime = CreateRuntime(out _);

        var result = await runtime.FetchLiveModelsAsync("sk-ant-oat01-example", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.Unsupported);
    }

    [Fact]
    public async Task FetchLiveModelsAsync_RestSuccess_ReturnsModels()
    {
        var handler = new StubHttpHandler();
        handler.Add("api.anthropic.com", HttpStatusCode.OK, """{"data":[{"id":"claude-sonnet-4"},{"id":"claude-haiku-4"}]}""");
        var runtime = CreateRuntime(out _, handler);

        var result = await runtime.FetchLiveModelsAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.Success);
        result.Models.Select(m => m.Id).ShouldBe(new[] { "claude-sonnet-4", "claude-haiku-4" });
    }

    [Fact]
    public async Task FetchLiveModelsAsync_Rest401_MapsToInvalidCredential()
    {
        var handler = new StubHttpHandler();
        handler.Add("api.anthropic.com", HttpStatusCode.Unauthorized, "{}");
        var runtime = CreateRuntime(out _, handler);

        var result = await runtime.FetchLiveModelsAsync("sk-ant-api03-bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.InvalidCredential);
    }

    // --- Helpers ---

    private static AgentRuntimeInstallConfig StandardConfig() =>
        new(
            Models: new[] { "claude-sonnet-4-20250514" },
            DefaultModel: "claude-sonnet-4-20250514",
            BaseUrl: null);

    private static ProbeStep GetStep(UnitValidationStep which)
    {
        var runtime = CreateRuntime(out _);
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-ant-api03-example");
        return steps.Single(s => s.Step == which);
    }

    private static ClaudeAgentRuntime CreateRuntime(
        out IHttpClientFactory httpClientFactory,
        StubHttpHandler? handler = null)
    {
        var actualHandler = handler ?? new StubHttpHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(actualHandler, disposeHandler: false));
        httpClientFactory = factory;
        return new ClaudeAgentRuntime(factory, NullLogger<ClaudeAgentRuntime>.Instance);
    }
}