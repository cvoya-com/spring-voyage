// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama.Tests;

using System.Net;

using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Behaviour tests for <see cref="OllamaAgentRuntime"/> after the T-03
/// probe-contract migration (#945). Ollama is credential-less, so the
/// probe plan only contains <see cref="UnitValidationStep.VerifyingTool"/>
/// and <see cref="UnitValidationStep.ResolvingModel"/>.
/// </summary>
public class OllamaAgentRuntimeTests
{
    [Fact]
    public void Identity_MatchesAcceptanceCriteria()
    {
        var runtime = BuildRuntime(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        runtime.Id.ShouldBe("ollama");
        runtime.DisplayName.ShouldBe("Ollama (dapr-agent + local Ollama)");
        runtime.ToolKind.ShouldBe("dapr-agent");
        runtime.CredentialSchema.Kind.ShouldBe(AgentRuntimeCredentialKind.None);
    }

    [Fact]
    public void DefaultModels_IsLoadedFromSeed()
    {
        var runtime = BuildRuntime(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        runtime.DefaultModels.Count.ShouldBeGreaterThan(0);
        runtime.DefaultModels.ShouldContain(d => d.Id == "llama3.2:3b");
    }

    // --- GetProbeSteps plan shape ---

    [Fact]
    public void GetProbeSteps_OmitsCredentialStep()
    {
        var runtime = BuildRuntime(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        var steps = runtime.GetProbeSteps(StandardConfig(), credential: string.Empty);

        steps.Select(s => s.Step).ShouldBe(new[]
        {
            UnitValidationStep.VerifyingTool,
            UnitValidationStep.ResolvingModel,
        });
        // Credential step intentionally skipped: Ollama is credential-less.
        steps.Select(s => s.Step).ShouldNotContain(UnitValidationStep.ValidatingCredential);
        steps.Select(s => s.Step).ShouldNotContain(UnitValidationStep.PullingImage);
    }

    [Fact]
    public void GetProbeSteps_AllTimeouts_AreBounded()
    {
        var runtime = BuildRuntime(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: string.Empty);

        foreach (var step in steps)
        {
            step.Timeout.ShouldBeGreaterThan(TimeSpan.Zero);
            step.Timeout.ShouldBeLessThan(TimeSpan.FromMinutes(5));
        }
    }

    // --- VerifyingTool ---

    [Fact]
    public void InterpretVerifyTool_Ok200_Succeeds()
    {
        var result = OllamaAgentRuntime.InterpretVerifyTool(0, "200", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretVerifyTool_CurlMissing_MapsToToolMissing()
    {
        var result = OllamaAgentRuntime.InterpretVerifyTool(127, string.Empty, "sh: curl: not found");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ToolMissing);
    }

    [Fact]
    public void InterpretVerifyTool_ServerDown_MapsToToolMissing()
    {
        var result = OllamaAgentRuntime.InterpretVerifyTool(0, "503", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ToolMissing);
    }

    // --- ResolvingModel ---

    [Fact]
    public void InterpretResolveModel_ModelPresent_SucceedsWithExtras()
    {
        var body = """{"models":[{"name":"llama3.2:3b"},{"name":"qwen2.5:7b"}]}""";
        var stdout = body + "\n200";

        var result = OllamaAgentRuntime.InterpretResolveModel(0, stdout, string.Empty, "llama3.2:3b");

        result.Outcome.ShouldBe(StepOutcome.Succeeded);
        result.Extras!["model"].ShouldBe("llama3.2:3b");
        result.Extras["models"].ShouldContain("llama3.2:3b");
    }

    [Fact]
    public void InterpretResolveModel_ModelMissing_MapsToModelNotFound()
    {
        var body = """{"models":[{"name":"llama3.2:3b"}]}""";
        var stdout = body + "\n200";

        var result = OllamaAgentRuntime.InterpretResolveModel(0, stdout, string.Empty, "ghost-model");

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ModelNotFound);
        result.Details!["models"].ShouldContain("llama3.2:3b");
    }

    [Fact]
    public void InterpretResolveModel_NonZeroExit_MapsToProbeInternalError()
    {
        var result = OllamaAgentRuntime.InterpretResolveModel(6, string.Empty, "connection refused", "llama3.2:3b");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ProbeInternalError);
    }

    // --- FetchLiveModelsAsync (still host-side) ---

    [Fact]
    public async Task FetchLiveModelsAsync_Ok_ReturnsModels()
    {
        var runtime = BuildRuntime(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":[{"name":"llama3.2:3b"}]}"""),
            })));

        var result = await runtime.FetchLiveModelsAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.Success);
        result.Models.Select(m => m.Id).ShouldContain("llama3.2:3b");
    }

    [Fact]
    public async Task FetchLiveModelsAsync_ServerUnreachable_ReturnsNetworkError()
    {
        var runtime = BuildRuntime(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        var result = await runtime.FetchLiveModelsAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.NetworkError);
    }

    // --- Helpers ---

    private static AgentRuntimeInstallConfig StandardConfig() =>
        new(
            Models: new[] { "llama3.2:3b" },
            DefaultModel: "llama3.2:3b",
            BaseUrl: "http://localhost:11434");

    private static OllamaAgentRuntime BuildRuntime(
        HttpMessageHandler handler,
        Action<OllamaAgentRuntimeOptions>? configure = null)
    {
        var options = new OllamaAgentRuntimeOptions();
        configure?.Invoke(options);

        return new OllamaAgentRuntime(
            new StubHttpClientFactory(handler),
            Options.Create(options));
    }
}