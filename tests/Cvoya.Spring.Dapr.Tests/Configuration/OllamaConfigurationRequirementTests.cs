// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class OllamaConfigurationRequirementTests
{
    [Fact]
    public async Task ValidateAsync_SuccessfulProbe_ReturnsMet()
    {
        var options = Options.Create(new OllamaOptions
        {
            BaseUrl = "http://ollama.test",
            RequireHealthyAtStartup = false,
            HealthCheckTimeoutSeconds = 1,
        });
        var factory = new StubHttpClientFactory((req, ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)));

        var requirement = new OllamaConfigurationRequirement(options, factory);
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task ValidateAsync_UnreachableOptional_ReturnsDisabled()
    {
        var options = Options.Create(new OllamaOptions
        {
            BaseUrl = "http://ollama.test",
            RequireHealthyAtStartup = false,
            HealthCheckTimeoutSeconds = 1,
        });
        var factory = new StubHttpClientFactory((req, ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("nope")));

        var requirement = new OllamaConfigurationRequirement(options, factory);
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Disabled);
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("could not reach");
    }

    [Fact]
    public async Task ValidateAsync_UnreachableRequired_ReturnsInvalidWithFatal()
    {
        var options = Options.Create(new OllamaOptions
        {
            BaseUrl = "http://ollama.test",
            RequireHealthyAtStartup = true,
            HealthCheckTimeoutSeconds = 1,
        });
        var factory = new StubHttpClientFactory((req, ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("nope")));

        var requirement = new OllamaConfigurationRequirement(options, factory);
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        requirement.IsMandatory.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_NonSuccessStatusOptional_ReturnsDisabled()
    {
        var options = Options.Create(new OllamaOptions
        {
            BaseUrl = "http://ollama.test",
            RequireHealthyAtStartup = false,
            HealthCheckTimeoutSeconds = 1,
        });
        var factory = new StubHttpClientFactory((req, ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var requirement = new OllamaConfigurationRequirement(options, factory);
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Disabled);
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("500");
    }

    private sealed class StubHttpClientFactory(
        System.Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubMessageHandler(handler));

        private sealed class StubMessageHandler(
            System.Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
        }
    }
}