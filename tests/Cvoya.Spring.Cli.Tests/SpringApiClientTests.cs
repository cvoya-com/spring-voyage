// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

public class SpringApiClientTests
{
    [Fact]
    public async Task ListAgentsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]");

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        var client = new SpringApiClient(httpClient);

        var result = await client.ListAgentsAsync(TestContext.Current.CancellationToken);

        result.ValueKind.Should().Be(JsonValueKind.Array);
        handler.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAgentAsync_SendsCorrectPayload()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"id":"ada","name":"Ada","role":"coder"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("id").GetString().Should().Be("ada");
                json.GetProperty("name").GetString().Should().Be("Ada");
                json.GetProperty("role").GetString().Should().Be("coder");
            });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        var client = new SpringApiClient(httpClient);

        var result = await client.CreateAgentAsync("ada", "Ada", "coder", TestContext.Current.CancellationToken);

        result.GetProperty("id").GetString().Should().Be("ada");
        handler.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task SendMessageAsync_SendsCorrectPayload()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/messages",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"messageId":"msg-1"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("to").GetProperty("scheme").GetString().Should().Be("agent");
                json.GetProperty("to").GetProperty("path").GetString().Should().Be("ada");
                json.GetProperty("text").GetString().Should().Be("Review PR #42");
            });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        var client = new SpringApiClient(httpClient);

        var result = await client.SendMessageAsync("agent", "ada", "Review PR #42", null, TestContext.Current.CancellationToken);

        result.GetProperty("messageId").GetString().Should().Be("msg-1");
        handler.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAgentAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada",
            expectedMethod: HttpMethod.Delete,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        var client = new SpringApiClient(httpClient);

        await client.DeleteAgentAsync("ada", TestContext.Current.CancellationToken);

        handler.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ListTokensAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/auth/tokens",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"name":"dev","createdAt":"2026-01-01"}]""");

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        var client = new SpringApiClient(httpClient);

        var result = await client.ListTokensAsync(TestContext.Current.CancellationToken);

        result.ValueKind.Should().Be(JsonValueKind.Array);
        handler.WasCalled.Should().BeTrue();
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

        request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
        request.Method.Should().Be(expectedMethod);

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
