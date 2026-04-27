// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text;

using Cvoya.Spring.AgentRuntimes.OpenAI;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>POST /api/v1/agent-runtimes/{id}/refresh-models</c>
/// (closes #720). Stubs the OpenAI named HttpClient's primary handler so the
/// endpoint exercises the real <c>OpenAiAgentRuntime.FetchLiveModelsAsync</c>
/// code path against a deterministic fake upstream.
/// </summary>
/// <remarks>
/// Uses a per-test <see cref="WebApplicationFactory{TEntryPoint}"/> derived
/// from <see cref="CustomWebApplicationFactory"/> so each case gets its own
/// in-memory database (avoiding install-row bleed between tests) and its
/// own stub upstream handler.
/// </remarks>
public sealed class AgentRuntimeRefreshModelsEndpointTests : IDisposable
{
    private readonly StubHandler _handler = new();
    private readonly RefreshModelsFactory _factory;
    private readonly HttpClient _client;

    public AgentRuntimeRefreshModelsEndpointTests()
    {
        _factory = new RefreshModelsFactory(_handler);
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Refresh_UnknownRuntime_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/not-a-real-runtime/refresh-models",
            new AgentRuntimeRefreshModelsRequest(Credential: "sk-x"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_RuntimeNotInstalled_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        // Fresh per-test factory — no install row exists for 'openai' yet.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/refresh-models",
            new AgentRuntimeRefreshModelsRequest(Credential: "sk-x"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_OpenAi_ReplacesTenantModelListWithLiveCatalog()
    {
        var ct = TestContext.Current.CancellationToken;

        // Install with a seed config whose model list does NOT match the
        // stubbed live catalog, so the assertion below proves the endpoint
        // actually wrote the new list.
        var install = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(
                Models: new[] { "stale-model-1", "stale-model-2" },
                DefaultModel: "stale-model-1",
                BaseUrl: null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        _handler.Respond(HttpStatusCode.OK, """
            {
              "object": "list",
              "data": [
                { "id": "gpt-4o", "object": "model" },
                { "id": "gpt-4o-mini", "object": "model" },
                { "id": "o4-mini", "object": "model" }
              ]
            }
            """);

        var refresh = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/refresh-models",
            new AgentRuntimeRefreshModelsRequest(Credential: "sk-good"),
            ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await refresh.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse>(ct);
        payload.ShouldNotBeNull();
        payload!.Models.ShouldBe(new[] { "gpt-4o", "gpt-4o-mini", "o4-mini" });
        // The stale default is not in the new list, so the endpoint should
        // reset DefaultModel to the first live entry.
        payload.DefaultModel.ShouldBe("gpt-4o");

        // Follow-up GET confirms the stored config survives beyond the
        // refresh response.
        var get = await _client.GetAsync("/api/v1/tenant/agent-runtimes/installs/openai", ct);
        var getBody = await get.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse>(ct);
        getBody.ShouldNotBeNull();
        getBody!.Models.ShouldBe(new[] { "gpt-4o", "gpt-4o-mini", "o4-mini" });

        // The outbound request should have carried the user's credential
        // and hit the provider's /v1/models endpoint.
        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/v1/models");
        _handler.LastRequest.Headers.GetValues("Authorization").ShouldContain("Bearer sk-good");
    }

    [Fact]
    public async Task Refresh_OpenAi_Preserves_DefaultModelWhenStillLive()
    {
        var ct = TestContext.Current.CancellationToken;

        // Pre-install with a default model that IS present in the refreshed
        // catalog below — the endpoint should preserve it.
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(
                Models: new[] { "gpt-4o", "old-sibling" },
                DefaultModel: "gpt-4o",
                BaseUrl: null),
            ct);

        _handler.Respond(HttpStatusCode.OK, """
            {
              "data": [
                { "id": "gpt-4o" },
                { "id": "gpt-4o-mini" }
              ]
            }
            """);

        var refresh = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/refresh-models",
            new AgentRuntimeRefreshModelsRequest(Credential: "sk-good"),
            ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await refresh.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse>(ct);
        payload.ShouldNotBeNull();
        payload!.DefaultModel.ShouldBe("gpt-4o");
    }

    [Fact]
    public async Task Refresh_OpenAi_Unauthorized_Returns401WithProblemDetails()
    {
        var ct = TestContext.Current.CancellationToken;

        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        _handler.Respond(HttpStatusCode.Unauthorized, """
            {"error":{"message":"Incorrect API key provided."}}
            """);

        var refresh = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/refresh-models",
            new AgentRuntimeRefreshModelsRequest(Credential: "sk-bad"),
            ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_OpenAi_ServerError_Returns502()
    {
        var ct = TestContext.Current.CancellationToken;

        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        _handler.Respond(HttpStatusCode.ServiceUnavailable, "upstream down");

        var refresh = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/refresh-models",
            new AgentRuntimeRefreshModelsRequest(Credential: "sk-anything"),
            ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// Custom factory that layers the shared stub HttpClient handler on top
    /// of the base test factory's Dapr-replacement plumbing.
    /// </summary>
    private sealed class RefreshModelsFactory(StubHandler handler) : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // Slot the test handler on the OpenAI named client as the
                // primary handler. The watchdog delegating handler layers
                // ABOVE it (AddHttpMessageHandler stacks on the primary),
                // so the stub still observes every outbound call and can
                // respond deterministically without touching the network.
                services
                    .AddHttpClient(OpenAiAgentRuntime.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.ServiceUnavailable;
        private string _body = "no response configured";

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}