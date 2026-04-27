// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http;

using Cvoya.Spring.AgentRuntimes.Claude;
using Cvoya.Spring.AgentRuntimes.Google;
using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.AgentRuntimes.OpenAI;
using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end tests that a 401 response flowing through each plugin-owned
/// named <see cref="HttpClient"/> wired by <c>Program.cs</c> flips the
/// <see cref="ICredentialHealthStore"/> row via the
/// <see cref="Cvoya.Spring.Dapr.CredentialHealth.CredentialHealthWatchdogHandler"/>.
/// </summary>
/// <remarks>
/// These tests are the guard against a future regression where the host
/// forgets to attach the watchdog to a new plugin's HttpClient. They exercise
/// the real Host.Api composition — if the wiring drifts (plugin rename,
/// handler not attached, wrong kind/subjectId), the mock
/// <see cref="ICredentialHealthStore"/> fails the Received(1) assertion.
/// </remarks>
public sealed class CredentialHealthWatchdogWiringTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ICredentialHealthStore _store = Substitute.For<ICredentialHealthStore>();

    public CredentialHealthWatchdogWiringTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("LocalDev", "true");
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");

                builder.ConfigureServices(services =>
                {
                    // Strip the Dapr Workflow IHostedService — same #568
                    // workaround CustomWebApplicationFactory applies. This
                    // test class drives WebApplicationFactory<Program>
                    // directly (it needs the real plugin HttpClient
                    // composition under test, not the mock-heavy stack
                    // CustomWebApplicationFactory builds), so the worker
                    // would otherwise spin up a gRPC stream, fail to reach
                    // a sidecar, and surface ObjectDisposedException on
                    // factory disposal — flaking ~one Theory iteration per
                    // CI run.
                    services.RemoveDaprWorkflowWorker();

                    // Swap the DbContext for in-memory. The watchdog write
                    // goes through ICredentialHealthStore which we replace
                    // below, so the in-memory context is just to keep other
                    // DI resolutions healthy.
                    var dbDescriptors = services
                        .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                                 || d.ServiceType == typeof(DbContextOptions)
                                 || d.ServiceType == typeof(SpringDbContext)
                                 || (d.ServiceType.FullName?.StartsWith(
                                        "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                                 || (d.ServiceType.FullName?.StartsWith(
                                        "Npgsql.", StringComparison.Ordinal) ?? false))
                        .ToList();
                    foreach (var d in dbDescriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddDbContext<SpringDbContext>(options =>
                        options.UseInMemoryDatabase($"WatchdogWiring_{Guid.NewGuid()}"));

                    // Replace the real store with a mock so the watchdog's
                    // write can be observed without going through EF.
                    var storeDescriptors = services
                        .Where(d => d.ServiceType == typeof(ICredentialHealthStore))
                        .ToList();
                    foreach (var d in storeDescriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(_store);

                    // Slot a stub primary handler on every wired named client
                    // so the outbound request returns a deterministic
                    // Unauthorized without hitting the network. The
                    // watchdog handler runs AFTER this primary handler
                    // because IHttpClientBuilder.AddHttpMessageHandler layers
                    // the DelegatingHandlers ABOVE the primary handler.
                    ConfigurePrimaryHandler(services, ClaudeAgentRuntime.HttpClientName);
                    ConfigurePrimaryHandler(services, GoogleAgentRuntime.HttpClientName);
                    ConfigurePrimaryHandler(services, OpenAiAgentRuntime.HttpClientName);
                    ConfigurePrimaryHandler(services, OllamaAgentRuntime.HttpClientName);
                    ConfigurePrimaryHandler(services, GitHubOAuthHttpClient.HttpClientName);
                    ConfigurePrimaryHandler(services, GitHubAppAuth.HttpClientName);
                    ConfigurePrimaryHandler(services, GitHubConnector.OctokitHttpClientName);
                });
            });
    }

    [Theory]
    [InlineData(ClaudeAgentRuntime.HttpClientName, CredentialHealthKind.AgentRuntime, ClaudeAgentRuntime.RuntimeId, "api-key")]
    [InlineData(GoogleAgentRuntime.HttpClientName, CredentialHealthKind.AgentRuntime, "google", "api-key")]
    [InlineData(OpenAiAgentRuntime.HttpClientName, CredentialHealthKind.AgentRuntime, "openai", "api-key")]
    [InlineData(OllamaAgentRuntime.HttpClientName, CredentialHealthKind.AgentRuntime, OllamaAgentRuntime.RuntimeId, "api-key")]
    [InlineData(GitHubOAuthHttpClient.HttpClientName, CredentialHealthKind.Connector, "github", "client-secret")]
    [InlineData(GitHubAppAuth.HttpClientName, CredentialHealthKind.Connector, "github", "private-key")]
    [InlineData(GitHubConnector.OctokitHttpClientName, CredentialHealthKind.Connector, "github", "private-key")]
    public async Task Plugin401Response_FlipsStoreToInvalid(
        string httpClientName,
        CredentialHealthKind expectedKind,
        string expectedSubjectId,
        string expectedSecretName)
    {
        var ct = TestContext.Current.CancellationToken;

        // Warm the factory so Program.cs has applied its DI including the
        // watchdog fan-out.
        using var _ = _factory.CreateClient();

        var httpFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpFactory.CreateClient(httpClientName);

        await client.GetAsync("https://example.invalid/watchdog-probe", ct);

        await _store.Received(1).RecordAsync(
            expectedKind,
            expectedSubjectId,
            expectedSecretName,
            CredentialHealthStatus.Invalid,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static void ConfigurePrimaryHandler(IServiceCollection services, string clientName)
    {
        services
            .AddHttpClient(clientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StaticResponseHandler(HttpStatusCode.Unauthorized));
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