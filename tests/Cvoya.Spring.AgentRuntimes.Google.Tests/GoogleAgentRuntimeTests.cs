// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google.Tests;

using System.Net;

using Cvoya.Spring.AgentRuntimes.Google;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Behaviour tests for <see cref="GoogleAgentRuntime"/> after the T-03
/// probe-contract migration (#945). Exercises the returned
/// <see cref="ProbeStep"/> plan + each step's InterpretOutput delegate with
/// small string fixtures matching what the in-container curl probe emits.
/// The FetchLiveModels path keeps its HTTP-backed tests because refresh
/// still runs host-side.
/// </summary>
public class GoogleAgentRuntimeTests
{
    private static readonly GoogleAgentRuntimeSeed TestSeed = new(
        Models: new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
        DefaultModel: "gemini-2.5-pro",
        BaseUrl: "https://generativelanguage.googleapis.com");

    [Fact]
    public void Identity_Surface_MatchesContract()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));

        runtime.Id.ShouldBe("google");
        runtime.DisplayName.ShouldBe("Google AI (dapr-agent + Google AI API)");
        runtime.ToolKind.ShouldBe("dapr-agent");
        runtime.CredentialSchema.Kind.ShouldBe(AgentRuntimeCredentialKind.ApiKey);
        runtime.CredentialSchema.DisplayHint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DefaultModels_LoadFromSeed()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));

        runtime.DefaultModels
            .Select(m => m.Id)
            .ShouldBe(new[] { "gemini-2.5-pro", "gemini-2.5-flash" });
    }

    // --- GetProbeSteps plan shape ---

    [Fact]
    public void GetProbeSteps_ReturnsToolCredentialModel_InOrder()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "AIzaSyTestKey");

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
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "AIzaSyTestKey");

        var credentialStep = steps.Single(s => s.Step == UnitValidationStep.ValidatingCredential);
        credentialStep.Env.ShouldContainKeyAndValue("GOOGLE_API_KEY", "AIzaSyTestKey");
    }

    [Fact]
    public void GetProbeSteps_AllTimeouts_AreBounded()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var steps = runtime.GetProbeSteps(StandardConfig(), credential: "key");

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
        var result = GoogleAgentRuntime.InterpretVerifyTool(0, "curl 8.4.0", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretVerifyTool_NonZero_MapsToToolMissing()
    {
        var result = GoogleAgentRuntime.InterpretVerifyTool(127, string.Empty, "sh: curl: not found");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ToolMissing);
    }

    // --- ValidatingCredential ---

    [Fact]
    public void InterpretValidateCredential_200_Succeeds()
    {
        var result = GoogleAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "200", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Succeeded);
    }

    [Fact]
    public void InterpretValidateCredential_401_MapsToCredentialInvalid()
    {
        var result = GoogleAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "401", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);
        result.Details!["http_status"].ShouldBe("401");
    }

    [Fact]
    public void InterpretValidateCredential_403_MapsToCredentialInvalid()
    {
        var result = GoogleAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "403", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);
    }

    [Fact]
    public void InterpretValidateCredential_400_MapsToCredentialFormatRejected()
    {
        var result = GoogleAgentRuntime.InterpretValidateCredentialFromHttpStatus(0, "400", string.Empty);
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.CredentialFormatRejected);
    }

    [Fact]
    public void InterpretValidateCredential_UnparseableStatus_MapsToProbeInternalError()
    {
        var result = GoogleAgentRuntime.InterpretValidateCredentialFromHttpStatus(6, "connection refused", "curl exit 6");
        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ProbeInternalError);
    }

    // --- ResolvingModel ---

    [Fact]
    public void InterpretResolveModel_200_SucceedsWithModelExtra()
    {
        var stdout = "{\"name\":\"models/gemini-2.5-pro\"}\n200";
        var result = GoogleAgentRuntime.InterpretResolveModel(0, stdout, string.Empty, "gemini-2.5-pro");

        result.Outcome.ShouldBe(StepOutcome.Succeeded);
        result.Extras!["model"].ShouldBe("gemini-2.5-pro");
    }

    [Fact]
    public void InterpretResolveModel_404_MapsToModelNotFound()
    {
        var stdout = "{\"error\":{\"message\":\"model not found\"}}\n404";
        var result = GoogleAgentRuntime.InterpretResolveModel(0, stdout, string.Empty, "gemini-ghost");

        result.Outcome.ShouldBe(StepOutcome.Failed);
        result.Code.ShouldBe(UnitValidationCodes.ModelNotFound);
    }

    // --- FetchLiveModelsAsync (HTTP-backed, still host-side) ---

    [Fact]
    public async Task FetchLiveModelsAsync_Empty_ReturnsInvalidCredential()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await runtime.FetchLiveModelsAsync("   ", TestContext.Current.CancellationToken);
        result.Status.ShouldBe(FetchLiveModelsStatus.InvalidCredential);
    }

    [Fact]
    public async Task FetchLiveModelsAsync_HttpOk_ReturnsModels()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"models":[{"name":"models/gemini-2.5-pro","displayName":"Gemini Pro"}]}"""),
        });

        var result = await runtime.FetchLiveModelsAsync("AIzaSyTestKey", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.Success);
        result.Models.Select(m => m.Id).ShouldContain("gemini-2.5-pro");
    }

    [Fact]
    public async Task FetchLiveModelsAsync_401_MapsToInvalidCredential()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"API key not valid."}}"""),
        });

        var result = await runtime.FetchLiveModelsAsync("bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(FetchLiveModelsStatus.InvalidCredential);
    }

    // --- Helpers ---

    private static AgentRuntimeInstallConfig StandardConfig() =>
        new(
            Models: new[] { "gemini-2.5-pro" },
            DefaultModel: "gemini-2.5-pro",
            BaseUrl: null);

    private static GoogleAgentRuntime BuildRuntime(Func<HttpRequestMessage, HttpResponseMessage> handle)
    {
        var handler = new StubHandler(handle);
        var client = new HttpClient(handler);
        var factory = new SingleClientHttpClientFactory(client);
        return new GoogleAgentRuntime(factory, NullLogger<GoogleAgentRuntime>.Instance, () => TestSeed);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handle(request));
        }
    }

    private sealed class SingleClientHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}