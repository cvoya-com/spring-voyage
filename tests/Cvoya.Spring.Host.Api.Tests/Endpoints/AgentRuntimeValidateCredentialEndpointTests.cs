// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.AgentRuntimes.OpenAI;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for
/// <c>POST /api/v1/agent-runtimes/{id}/validate-credential</c>
/// (closes #1066). Verifies the endpoint:
///   * probes the runtime via the existing live-catalog code path;
///   * does NOT touch the tenant's stored model list (the dedicated
///     separation from <c>refresh-models</c> is the whole point of #1066);
///   * records the outcome in the credential-health store so subsequent
///     reads from <c>spring agent-runtime credentials status</c> reflect
///     the latest probe;
///   * short-circuits credential-less runtimes (e.g. Ollama) without
///     persisting a row.
/// </summary>
public sealed class AgentRuntimeValidateCredentialEndpointTests : IDisposable
{
    // Host.Api serialises enums as strings via Program.cs's
    // JsonStringEnumConverter registration; reads must match or
    // ReadFromJsonAsync fails with "cannot convert $.status".
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly StubHandler _handler = new();
    private readonly ValidateCredentialFactory _factory;
    private readonly HttpClient _client;

    public AgentRuntimeValidateCredentialEndpointTests()
    {
        _factory = new ValidateCredentialFactory(_handler);
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Validate_UnknownRuntime_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/not-a-real-runtime/validate-credential",
            new AgentRuntimeValidateCredentialRequest("sk-x", null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Validate_OpenAi_Success_RecordsHealthAndReturnsValid()
    {
        var ct = TestContext.Current.CancellationToken;

        // Pre-install the runtime with a stable seed model list. The
        // assertion at the end of this test confirms the endpoint did NOT
        // rotate it — that's the whole separation of concerns from
        // refresh-models that #1066 introduces.
        var seed = new[] { "stable-1", "stable-2" };
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(seed, "stable-1", null),
            ct);

        _handler.Respond(HttpStatusCode.OK, """
            { "object": "list", "data": [ { "id": "gpt-4o" } ] }
            """);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/validate-credential",
            new AgentRuntimeValidateCredentialRequest("sk-good", null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AgentRuntimeValidateCredentialResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload!.Ok.ShouldBeTrue();
        payload.Status.ShouldBe(CredentialHealthStatus.Valid);
        payload.Detail.ShouldBeNull();
        payload.ValidatedAt.ShouldBeGreaterThan(DateTimeOffset.MinValue);

        // Health-store row should now exist and match the probe outcome.
        var statusResponse = await _client.GetAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/credential-health",
            ct);
        statusResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var healthRow = await statusResponse.Content.ReadFromJsonAsync<CredentialHealthResponse>(JsonOptions, ct);
        healthRow.ShouldNotBeNull();
        healthRow!.Status.ShouldBe(CredentialHealthStatus.Valid);

        // The model list must be unchanged — `validate-credential` is
        // explicitly NOT a catalog-rotation surface.
        var configResponse = await _client.GetAsync("/api/v1/tenant/agent-runtimes/installs/openai", ct);
        var config = await configResponse.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse>(ct);
        config.ShouldNotBeNull();
        config!.Models.ShouldBe(seed);
        config.DefaultModel.ShouldBe("stable-1");
    }

    [Fact]
    public async Task Validate_OpenAi_Unauthorized_RecordsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        _handler.Respond(HttpStatusCode.Unauthorized, """
            {"error":{"message":"Incorrect API key provided."}}
            """);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/validate-credential",
            new AgentRuntimeValidateCredentialRequest("sk-bad", null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AgentRuntimeValidateCredentialResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload!.Ok.ShouldBeFalse();
        payload.Status.ShouldBe(CredentialHealthStatus.Invalid);
        payload.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Validate_OpenAi_NetworkError_DoesNotFlipPersistentValidRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        // First, prime the row to Valid so we can confirm the subsequent
        // network blip does NOT regress it.
        _handler.Respond(HttpStatusCode.OK, """{ "data": [ { "id": "gpt-4o" } ] }""");
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/validate-credential",
            new AgentRuntimeValidateCredentialRequest("sk-good", null),
            ct);

        _handler.Respond(HttpStatusCode.ServiceUnavailable, "upstream down");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/validate-credential",
            new AgentRuntimeValidateCredentialRequest("sk-anything", null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AgentRuntimeValidateCredentialResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload!.Ok.ShouldBeFalse();
        // Persistent status reported in the response is whatever the row
        // *would* be — we deliberately do NOT overwrite Valid with Unknown
        // on a transient transport blip (matches the connector
        // validate-credential behaviour and the HTTP watchdog rules).
        payload.Status.ShouldBe(CredentialHealthStatus.Unknown);

        var statusResponse = await _client.GetAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/credential-health",
            ct);
        var row = await statusResponse.Content.ReadFromJsonAsync<CredentialHealthResponse>(JsonOptions, ct);
        row.ShouldNotBeNull();
        row!.Status.ShouldBe(CredentialHealthStatus.Valid);
    }

    [Fact]
    public async Task Validate_CredentiallessRuntime_Ollama_ShortCircuits()
    {
        var ct = TestContext.Current.CancellationToken;
        // Ollama declares CredentialKind.None so the endpoint should
        // short-circuit without touching the upstream HTTP handler or
        // recording a credential-health row.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/ollama/validate-credential",
            new AgentRuntimeValidateCredentialRequest(null, null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AgentRuntimeValidateCredentialResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload!.Ok.ShouldBeFalse();
        payload.Status.ShouldBe(CredentialHealthStatus.Unknown);
        payload.Detail.ShouldNotBeNull();
        payload.Detail!.ShouldContain("does not require credentials");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed class ValidateCredentialFactory(StubHandler handler) : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
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

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}