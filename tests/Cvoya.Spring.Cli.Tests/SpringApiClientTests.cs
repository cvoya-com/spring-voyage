// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.Net;
using System.Text.Json;

using Shouldly;

using Xunit;

public class SpringApiClientTests
{
    private const string BaseUrl = "http://localhost:5000";

    [Fact]
    public async Task ListAgentsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListAgentsAsync(TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAgentAsync_SendsContractFieldsAndDeserialisesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"id":"ada","name":"ada","displayName":"Ada","role":"coder"}""",
            validateRequestBody: body =>
            {
                // Kiota's JSON writer mirrors the OpenAPI contract: name → CreateAgentRequest.Name
                // (the unique identifier on the wire), displayName → CreateAgentRequest.DisplayName.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("name").GetString().ShouldBe("ada");
                json.GetProperty("displayName").GetString().ShouldBe("Ada");
                json.GetProperty("role").GetString().ShouldBe("coder");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.CreateAgentAsync("ada", "Ada", "coder", TestContext.Current.CancellationToken);

        result.Id.ShouldBe("ada");
        result.DisplayName.ShouldBe("Ada");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessageAsync_WrapsTextAsDomainPayload()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"messageId":"{{Guid.NewGuid()}}"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("to").GetProperty("scheme").GetString().ShouldBe("agent");
                json.GetProperty("to").GetProperty("path").GetString().ShouldBe("ada");
                json.GetProperty("type").GetString().ShouldBe("Domain");
                json.GetProperty("payload").GetString().ShouldBe("Review PR #42");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.SendMessageAsync("agent", "ada", "Review PR #42", null, TestContext.Current.CancellationToken);

        result.MessageId.ShouldNotBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAgentAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada",
            expectedMethod: HttpMethod.Delete,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.DeleteAgentAsync("ada", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListTokensAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/auth/tokens",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"name":"dev","createdAt":"2026-01-01T00:00:00Z"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListTokensAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("dev");
        handler.WasCalled.ShouldBeTrue();
    }
}

/// <summary>
/// Test double for HttpMessageHandler that validates requests and returns configured responses.
/// </summary>
internal class MockHttpMessageHandler(
    string expectedPath,
    HttpMethod expectedMethod,
    string responseBody,
    HttpStatusCode returnStatusCode = HttpStatusCode.OK,
    Action<string>? validateRequestBody = null) : HttpMessageHandler
{
    public bool WasCalled { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasCalled = true;

        request.RequestUri!.AbsolutePath.ShouldBe(expectedPath);
        request.Method.ShouldBe(expectedMethod);

        if (validateRequestBody is not null && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            validateRequestBody(body);
        }

        var response = new HttpResponseMessage(returnStatusCode);

        if (!string.IsNullOrEmpty(responseBody))
        {
            response.Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json");
        }

        return response;
    }
}