// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama.Tests;

using System.Net;

using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

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

    [Fact]
    public async Task ValidateCredentialAsync_ReachableEndpoint_ReturnsValid()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.ShouldBe("/api/tags");
            req.Method.ShouldBe(HttpMethod.Get);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var runtime = BuildRuntime(handler);

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        result.Valid.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ValidateCredentialAsync_NetworkFailure_ReturnsNetworkError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection refused"));

        var runtime = BuildRuntime(handler);

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ValidateCredentialAsync_ProxyAuthRequired_ReturnsInvalid()
    {
        // 401/403 against /api/tags is unusual but we deliberately surface
        // it as Invalid so the wizard's message says "the proxy rejected
        // the request" rather than "the network is down".
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var runtime = BuildRuntime(handler);

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateCredentialAsync_ServerError_ReturnsNetworkError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var runtime = BuildRuntime(handler);

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateCredentialAsync_EmptyBaseUrl_ReturnsInvalid()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var runtime = BuildRuntime(handler, options =>
        {
            options.BaseUrl = string.Empty;
        });

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("BaseUrl is empty");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateCredentialAsync_MalformedBaseUrl_ReturnsInvalid()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var runtime = BuildRuntime(handler, options =>
        {
            options.BaseUrl = "not-a-uri";
        });

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("not a valid absolute URI");
    }

    [Fact]
    public async Task ValidateCredentialAsync_IgnoresSuppliedCredential()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            req.Headers.Authorization.ShouldBeNull();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var runtime = BuildRuntime(handler);

        var result = await runtime.ValidateCredentialAsync("sk-ignored", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_ReachableEndpoint_Passes()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var runtime = BuildRuntime(handler);

        var result = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_UnreachableEndpoint_FailsWithMessage()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection refused"));

        var runtime = BuildRuntime(handler);

        var result = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
        result.Errors[0].ShouldContain("not reachable");
        result.Errors[0].ShouldContain("connection refused");
    }

    [Fact]
    public async Task ValidateCredentialAsync_HitsConfiguredBaseUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            req.RequestUri!.Host.ShouldBe("custom-host");
            req.RequestUri.Port.ShouldBe(11434);
            req.RequestUri.AbsolutePath.ShouldBe("/api/tags");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var runtime = BuildRuntime(handler, options =>
        {
            options.BaseUrl = "http://custom-host:11434";
        });

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ValidateCredentialAsync_CallerCancelled_PropagatesCancellation()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var runtime = BuildRuntime(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => runtime.ValidateCredentialAsync(string.Empty, cts.Token));
    }

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