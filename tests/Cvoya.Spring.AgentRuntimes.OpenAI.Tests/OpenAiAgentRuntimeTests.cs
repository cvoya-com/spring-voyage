// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.OpenAI.Tests;

using System.Net;
using System.Text;

using Cvoya.Spring.AgentRuntimes.OpenAI;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Behaviour tests for <see cref="OpenAiAgentRuntime"/> after the T-03
/// probe-contract migration (#945). Covers the returned
/// <see cref="ProbeStep"/> plan shape + each step's InterpretOutput
/// delegate, plus the still-host-side FetchLiveModels path.
/// </summary>
public class OpenAiAgentRuntimeTests
{
    private static readonly OpenAiAgentRuntimeSeed TestSeed = new(
        Models: new[] { "gpt-4o", "gpt-4o-mini", "o3-mini" },
        DefaultModel: "gpt-4o",
        BaseUrl: "https://api.openai.com");

    [Fact]
    public void Identity_MatchesContract()
    {
        var runtime = CreateRuntime(new StubHandler());

        runtime.Id.ShouldBe("openai");
        runtime.DisplayName.ShouldBe("OpenAI (dapr-agent + OpenAI API)");
        runtime.ToolKind.ShouldBe("dapr-agent");
    }

    [Fact]
    public void CredentialSchema_IsApiKey_WithDisplayHint()
    {
        var runtime = CreateRuntime(new StubHandler());

        runtime.CredentialSchema.Kind.ShouldBe(AgentRuntimeCredentialKind.ApiKey);
        runtime.CredentialSchema.DisplayHint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DefaultModels_MatchesSeed()
    {
        var runtime = CreateRuntime(new StubHandler());

        runtime.DefaultModels.Select(m => m.Id).ShouldBe(new[] { "gpt-4o", "gpt-4o-mini", "o3-mini" });
        runtime.DefaultModels.ShouldAllBe(m => m.ContextWindow == null);
    }

    // --- GetProbeSteps plan shape ---

    [Fact]
    public void GetProbeSteps_ReturnsToolCredentialModel_InOrder()
    {
        var runtime = CreateRuntime(new StubHandler());
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-test");

        steps.Select(s => s.Step).ShouldBe(new[]
        {
            UnitValidationStep.VerifyingTool,
            UnitValidationStep.ValidatingCredential,
            UnitValidationStep.ResolvingModel,
        });
        steps.Select(s => s.Step).ShouldNotContain(UnitValidationStep.PullingImage);
    }

    [Fact]
    public void GetProbeSteps_CredentialStep_PopulatesEnvVar()
    {
        var runtime = CreateRuntime(new StubHandler());
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-test-123");

        var credentialStep = steps.Single(s => s.Step == UnitValidationStep.ValidatingCredential);
        credentialStep.Env.ShouldContainKeyAndValue("OPENAI_API_KEY", "sk-test-123");
    }

    [Fact]
    public void GetProbeSteps_AllTimeouts_AreBounded()
    {
        var runtime = CreateRuntime(new StubHandler());
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "sk-test");

        foreach (var step in steps)
        {
            step.Timeout.ShouldBeGreaterThan(TimeSpan.Zero);
            step.Timeout.ShouldBeLessThan(TimeSpan.FromMinutes(5));
        }
    }

    // --- VerifyingTool ---

    [Fact]
    public void InterpretVerifyTool_ExitZero_Succeeds()
    {
        var result = OpenAiAgentRuntime.InterpretVerifyTool(0, "curl 8.4.0", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretVerifyTool_NonZero_MapsToToolMissing()
    {
        var result = OpenAiAgentRuntime.InterpretVerifyTool(127, string.Empty, "sh: curl: not found");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ToolMissing);
    }

    // --- ValidatingCredential ---

    [Fact]
    public void InterpretValidateCredential_200_Succeeds()
    {
        var result = OpenAiAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "200", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretValidateCredential_401_MapsToCredentialInvalid()
    {
        var result = OpenAiAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "401", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);
        result.Details!["http_status"].ShouldBe("401");
    }

    [Fact]
    public void InterpretValidateCredential_400_MapsToCredentialFormatRejected()
    {
        var result = OpenAiAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "400", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialFormatRejected);
    }

    [Fact]
    public void InterpretValidateCredential_UnparseableStatus_MapsToProbeInternalError()
    {
        var result = OpenAiAgentRuntime.InterpretValidateCredentialFromHttpStatus(6, "curl exit 6 connection refused", "");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ProbeInternalError);
    }

    // --- ResolvingModel ---

    [Fact]
    public void InterpretResolveModel_200_SucceedsWithModelExtra()
    {
        var stdout = "{\"id\":\"gpt-4o\"}\n200";
        var result = OpenAiAgentRuntime.InterpretResolveModel(0, stdout, string.Empty, "gpt-4o");
        result.Outcome.ShouldBe(StepOutcome.Succeeded);
        result.Extras!["model"].ShouldBe("gpt-4o");
    }

    [Fact]
    public void InterpretResolveModel_404_MapsToModelNotFound()
    {
        var stdout = "{\"error\":{\"message\":\"model not found\"}}\n404";
        var result = OpenAiAgentRuntime.InterpretResolveModel(0, stdout, string.Empty, "gpt-ghost");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ModelNotFound);
    }

    // --- FetchLiveModelsAsync (HTTP-backed, host-side) ---

    [Fact]
    public async Task FetchLiveModelsAsync_Empty_ReturnsInvalidCredential()
    {
        var handler = new StubHandler();
        var runtime = CreateRuntime(handler);
        var result = await runtime.FetchLiveModelsAsync("   ", TestContext.Current.CancellationToken);
        result.Status.ShouldBe(FetchLiveModelsStatus.InvalidCredential);
    }

    [Fact]
    public async Task FetchLiveModelsAsync_HttpOk_ReturnsModels()
    {
        var handler = new StubHandler();
        handler.Add("api.openai.com", HttpStatusCode.OK, """{"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"}]}""");
        var runtime = CreateRuntime(handler);

        var result = await runtime.FetchLiveModelsAsync("sk-test", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.Success);
        result.Models.Select(m => m.Id).ShouldBe(new[] { "gpt-4o", "gpt-4o-mini" });
    }

    [Fact]
    public async Task FetchLiveModelsAsync_401_MapsToInvalidCredential()
    {
        var handler = new StubHandler();
        handler.Add("api.openai.com", HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key."}}""");
        var runtime = CreateRuntime(handler);

        var result = await runtime.FetchLiveModelsAsync("bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.InvalidCredential);
    }

    // --- Helpers ---

    private static AgentRuntimeInstallConfig StandardConfig() =>
        new(
            Models: new[] { "gpt-4o" },
            DefaultModel: "gpt-4o",
            BaseUrl: null);

    private static OpenAiAgentRuntime CreateRuntime(HttpMessageHandler handler, OpenAiAgentRuntimeSeed? seed = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new OpenAiAgentRuntime(
            factory,
            NullLogger<OpenAiAgentRuntime>.Instance,
            () => seed ?? TestSeed);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public void Add(string host, HttpStatusCode status, string body) =>
            _responses[host] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            var host = request.RequestUri?.Host ?? string.Empty;
            if (!_responses.TryGetValue(host, out var r))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"no stub for {host}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(r.Status)
            {
                Content = new StringContent(r.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}