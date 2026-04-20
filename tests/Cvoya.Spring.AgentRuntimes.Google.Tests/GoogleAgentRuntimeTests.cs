// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google.Tests;

using System.Net;

using Cvoya.Spring.AgentRuntimes.Google;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

public class GoogleAgentRuntimeTests
{
    private static readonly GoogleAgentRuntimeSeed TestSeed = new(
        Models: new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
        DefaultModel: "gemini-2.5-pro",
        BaseUrl: "https://generativelanguage.googleapis.com");

    [Fact]
    public void Identity_Surface_MatchesIssue681()
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

    [Fact]
    public async Task ValidateCredentialAsync_Empty_ReturnsInvalid_WithoutNetworkCall()
    {
        var sentRequests = new List<HttpRequestMessage>();
        var runtime = BuildRuntime(req =>
        {
            sentRequests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await runtime.ValidateCredentialAsync("   ", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        sentRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateCredentialAsync_HttpOk_ReturnsValid()
    {
        HttpRequestMessage? captured = null;
        var runtime = BuildRuntime(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"models\": [] }"),
            };
        });

        var result = await runtime.ValidateCredentialAsync("AIzaSyTestKey", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        result.Valid.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Get);
        captured.RequestUri.ShouldNotBeNull();
        captured.RequestUri!.AbsoluteUri.ShouldStartWith(
            "https://generativelanguage.googleapis.com/v1beta/models?key=");
        captured.RequestUri.Query.ShouldContain("AIzaSyTestKey");
    }

    [Fact]
    public async Task ValidateCredentialAsync_HttpUnauthorized_ReturnsInvalid()
    {
        var runtime = BuildRuntime(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{ \"error\": { \"message\": \"API key not valid.\" } }"),
            });

        var result = await runtime.ValidateCredentialAsync("bad-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage!.ShouldContain("Google");
        result.ErrorMessage.ShouldContain("401");
        result.ErrorMessage.ShouldContain("API key not valid.");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ValidateCredentialAsync_HttpClientError_ReturnsInvalid(HttpStatusCode statusCode)
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(statusCode));

        var result = await runtime.ValidateCredentialAsync("some-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ValidateCredentialAsync_HttpServerError_ReturnsNetworkError(HttpStatusCode statusCode)
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(statusCode));

        var result = await runtime.ValidateCredentialAsync("some-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage!.ShouldContain("transient");
    }

    [Fact]
    public async Task ValidateCredentialAsync_HttpRequestException_ReturnsNetworkError()
    {
        var runtime = BuildRuntime(_ => throw new HttpRequestException("DNS failure"));

        var result = await runtime.ValidateCredentialAsync("some-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage!.ShouldContain("DNS failure");
    }

    [Fact]
    public async Task ValidateCredentialAsync_TimeoutException_ReturnsNetworkError()
    {
        var runtime = BuildRuntime(_ => throw new TaskCanceledException("Timed out"));

        var result = await runtime.ValidateCredentialAsync("some-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateCredentialAsync_CallerCancellation_Propagates()
    {
        var runtime = BuildRuntime(_ => throw new TaskCanceledException("Cancelled"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => runtime.ValidateCredentialAsync("some-key", cts.Token));
    }

    [Fact]
    public async Task ValidateCredentialAsync_KeyIsUrlEscapedInQuery()
    {
        HttpRequestMessage? captured = null;
        var runtime = BuildRuntime(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await runtime.ValidateCredentialAsync("a b&c=d", TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.RequestUri.ShouldNotBeNull();
        var query = captured.RequestUri!.Query;
        // The literal characters '&' and '=' would corrupt the query string;
        // they must be percent-encoded so the API receives a single key parameter.
        query.ShouldContain("a%20b%26c%3Dd");
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_ReturnsResult_WithoutThrowing()
    {
        var runtime = BuildRuntime(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var baseline = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        baseline.ShouldNotBeNull();
        // Either passes (Dapr.Actors loaded — true in integration tests) or
        // surfaces a single explanatory error. The contract guarantees one
        // entry per failed check.
        if (baseline.Passed)
        {
            baseline.Errors.ShouldBeEmpty();
        }
        else
        {
            baseline.Errors.ShouldNotBeEmpty();
            baseline.Errors.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e));
        }
    }

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