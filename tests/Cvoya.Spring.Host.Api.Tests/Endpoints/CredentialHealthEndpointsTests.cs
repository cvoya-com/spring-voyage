// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the validate-credential + credential-health
/// endpoints exposed under <c>/api/v1/agent-runtimes/{id}/</c> and
/// <c>/api/v1/connectors/{slugOrId}/</c>. Focuses on the round-trip:
/// POST validate-credential → GET credential-health reflects the
/// recorded status.
/// </summary>
public class CredentialHealthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    // Host.Api serialises enums as strings via Program.cs's
    // JsonStringEnumConverter registration. Reads have to match or
    // ReadFromJsonAsync fails with "cannot convert $.status".
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly HttpClient _client;

    public CredentialHealthEndpointsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAgentRuntimeCredentialHealth_NoRow_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        // Use a secretName unlikely to collide with other tests.
        var response = await _client.GetAsync(
            "/api/v1/agent-runtimes/claude/credential-health?secretName=probe-404",
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentRuntimeCredentialHealth_UnknownRuntime_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync(
            "/api/v1/agent-runtimes/not-a-real-runtime/credential-health", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ValidateAgentRuntimeCredential_RecordsHealthRow()
    {
        var ct = TestContext.Current.CancellationToken;
        // Ollama's ValidateCredentialAsync returns Valid for any string
        // when OLLAMA_URL is reachable; but we can't rely on that in
        // tests. Instead hit Claude — its validator should at least
        // return a deterministic non-NetworkError outcome when passed a
        // fake credential (Claude runtime maps invalid shape to
        // Invalid). Use a distinct secretName so the row is isolated.
        var secretName = $"probe-{Guid.NewGuid():N}";
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/claude/validate-credential",
            new CredentialValidateRequest("sk-ant-definitely-not-valid", secretName),
            ct);

        postResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var postBody = await postResponse.Content
            .ReadFromJsonAsync<CredentialValidateResponse>(JsonOptions, ct);
        postBody.ShouldNotBeNull();

        // Whether the runtime returned NetworkError (no internet during
        // tests) or Invalid depends on the sandbox; the endpoint MUST
        // return a parseable body either way. Only a non-NetworkError
        // outcome persists a row.
        if (postBody!.Status == CredentialHealthStatus.Unknown)
        {
            return;
        }

        var getResponse = await _client.GetAsync(
            $"/api/v1/agent-runtimes/claude/credential-health?secretName={secretName}",
            ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var getBody = await getResponse.Content
            .ReadFromJsonAsync<CredentialHealthResponse>(JsonOptions, ct);
        getBody.ShouldNotBeNull();
        getBody!.SubjectId.ShouldBe("claude");
        getBody.SecretName.ShouldBe(secretName);
        getBody.Status.ShouldBe(postBody.Status);
    }

    [Fact]
    public async Task ValidateConnectorCredential_NoAuthConnector_ReturnsUnknown()
    {
        // The factory registers a stub connector that does not override
        // ValidateCredentialAsync — so the default no-op returns null, and
        // the endpoint surfaces an Unknown status with a friendly message.
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsJsonAsync(
            "/api/v1/connectors/stub/validate-credential",
            new CredentialValidateRequest("anything", SecretName: null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content
            .ReadFromJsonAsync<CredentialValidateResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Valid.ShouldBeFalse();
        body.Status.ShouldBe(CredentialHealthStatus.Unknown);
        body.ErrorMessage.ShouldNotBeNull();
        body.ErrorMessage!.ShouldContain("does not require credentials");
    }

    [Fact]
    public async Task GetConnectorCredentialHealth_UnknownSlug_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync(
            "/api/v1/connectors/not-a-real-connector/credential-health", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}