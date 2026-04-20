// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.CredentialHealth;

using System.Net;

using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Dapr.CredentialHealth;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="CredentialHealthWatchdogHandler"/>. Covers
/// the status-code → <see cref="CredentialHealthStatus"/> mapping and
/// the fail-soft behaviour when the store write throws.
/// </summary>
public class CredentialHealthWatchdogHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CredentialHealthStatus.Invalid)]
    [InlineData(HttpStatusCode.Forbidden, CredentialHealthStatus.Revoked)]
    public async Task SendAsync_FlipsStoreOnAuthFailure(
        HttpStatusCode statusCode,
        CredentialHealthStatus expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<ICredentialHealthStore>();
        var client = BuildClient(store, statusCode);

        await client.GetAsync("https://example.com/test", ct);

        await store.Received(1).RecordAsync(
            CredentialHealthKind.AgentRuntime,
            "openai",
            "api-key",
            expected,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendAsync_DoesNotTouchStoreOnNonAuthStatus(HttpStatusCode statusCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<ICredentialHealthStore>();
        var client = BuildClient(store, statusCode);

        await client.GetAsync("https://example.com/test", ct);

        await store.DidNotReceive().RecordAsync(
            Arg.Any<CredentialHealthKind>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CredentialHealthStatus>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_StoreThrows_ResponseStillReturned()
    {
        // Fail-soft: the watchdog must never affect the upstream HTTP
        // response, even if its own bookkeeping write throws.
        var ct = TestContext.Current.CancellationToken;
        var store = Substitute.For<ICredentialHealthStore>();
        store.RecordAsync(
                Arg.Any<CredentialHealthKind>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CredentialHealthStatus>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Core.CredentialHealth.CredentialHealth>(
                new InvalidOperationException("boom")));
        var client = BuildClient(store, HttpStatusCode.Unauthorized);

        var response = await client.GetAsync("https://example.com/test", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpClient BuildClient(ICredentialHealthStore store, HttpStatusCode responseStatus)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);

        var handler = new CredentialHealthWatchdogHandler(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            CredentialHealthKind.AgentRuntime,
            subjectId: "openai",
            secretName: "api-key",
            NullLogger<CredentialHealthWatchdogHandler>.Instance)
        {
            InnerHandler = new StaticResponseHandler(responseStatus),
        };

        return new HttpClient(handler);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StaticResponseHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status));
    }
}