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

    // --- #320: unit membership wrappers ---

    [Fact]
    public async Task ListUnitMembershipsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/memberships",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"unitId":"eng-team","agentAddress":"ada","model":null,"specialty":null,"enabled":true,"executionMode":null,"createdAt":"2026-04-01T00:00:00Z","updatedAt":"2026-04-01T00:00:00Z"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListUnitMembershipsAsync("eng-team", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].UnitId.ShouldBe("eng-team");
        result[0].AgentAddress.ShouldBe("ada");
        result[0].Enabled.ShouldBe(true);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAgentMembershipsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada/memberships",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"unitId":"eng-team","agentAddress":"ada","model":null,"specialty":null,"enabled":true,"executionMode":null,"createdAt":"2026-04-01T00:00:00Z","updatedAt":"2026-04-01T00:00:00Z"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListAgentMembershipsAsync("ada", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].AgentAddress.ShouldBe("ada");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpsertMembershipAsync_SendsOverridesAndParsesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/memberships/ada",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"unitId":"eng-team","agentAddress":"ada","model":"claude-opus-4","specialty":"coding","enabled":true,"executionMode":"OnDemand","createdAt":"2026-04-01T00:00:00Z","updatedAt":"2026-04-01T00:00:00Z"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("model").GetString().ShouldBe("claude-opus-4");
                json.GetProperty("specialty").GetString().ShouldBe("coding");
                json.GetProperty("enabled").GetBoolean().ShouldBeTrue();
                json.GetProperty("executionMode").GetString().ShouldBe("OnDemand");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.UpsertMembershipAsync(
            "eng-team",
            "ada",
            model: "claude-opus-4",
            specialty: "coding",
            enabled: true,
            executionMode: Cvoya.Spring.Cli.Generated.Models.AgentExecutionMode.OnDemand,
            TestContext.Current.CancellationToken);

        result.UnitId.ShouldBe("eng-team");
        result.AgentAddress.ShouldBe("ada");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpsertMembershipAsync_OmitsExecutionModeWhenNull()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/memberships/ada",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"unitId":"eng-team","agentAddress":"ada","model":null,"specialty":null,"enabled":true,"executionMode":null,"createdAt":"2026-04-01T00:00:00Z","updatedAt":"2026-04-01T00:00:00Z"}""",
            validateRequestBody: body =>
            {
                // When callers pass no --execution-mode, the request must not force a
                // value on the server — the property either stays out of the payload
                // or round-trips as JSON null so the server keeps the current override.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                if (json.TryGetProperty("executionMode", out var executionMode))
                {
                    executionMode.ValueKind.ShouldBe(JsonValueKind.Null);
                }
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.UpsertMembershipAsync(
            "eng-team",
            "ada",
            model: null,
            specialty: null,
            enabled: null,
            executionMode: null,
            TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteMembershipAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/memberships/ada",
            expectedMethod: HttpMethod.Delete,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.DeleteMembershipAsync("eng-team", "ada", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    // --- #376: QueryActivityAsync -------------------------------------------

    [Fact]
    public async Task QueryActivityAsync_CallsCorrectEndpointAndParsesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/activity",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"items":[{"id":"00000000-0000-0000-0000-000000000001","source":"unit://eng-team","eventType":"StateChanged","severity":"Info","summary":"Unit started","correlationId":null,"cost":0.0042,"timestamp":"2026-04-01T00:00:00Z"}],"totalCount":1,"page":1,"pageSize":50}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.QueryActivityAsync(ct: TestContext.Current.CancellationToken);

        result.Items.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].Source.ShouldBe("unit://eng-team");
        result.Items[0].EventType.ShouldBe("StateChanged");
        result.Items[0].Severity.ShouldBe("Info");
        result.Items[0].Summary.ShouldBe("Unit started");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task QueryActivityAsync_WithFilters_PassesQueryParameters()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/activity",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"items":[],"totalCount":0,"page":1,"pageSize":10}""",
            validateQuery: query =>
            {
                query.ShouldContain("Source=unit%3Aeng-team");
                query.ShouldContain("Severity=Warning");
                query.ShouldContain("PageSize=10");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.QueryActivityAsync(
            source: "unit:eng-team",
            severity: "Warning",
            pageSize: 10,
            ct: TestContext.Current.CancellationToken);

        result.Items.ShouldNotBeNull();
        result.Items.Count.ShouldBe(0);
        handler.WasCalled.ShouldBeTrue();
    }

    // --- #315: CreateUnitAsync forwards model + color ---------------------

    [Fact]
    public async Task CreateUnitAsync_WithModelAndColor_SendsBothOnRequestBody()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"id":"actor-eng","name":"eng-team","displayName":"eng-team","description":"","registeredAt":"2026-04-01T00:00:00Z","status":"Draft","model":"claude-sonnet-4","color":"#6366f1"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("name").GetString().ShouldBe("eng-team");
                json.GetProperty("model").GetString().ShouldBe("claude-sonnet-4");
                json.GetProperty("color").GetString().ShouldBe("#6366f1");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.CreateUnitAsync(
            "eng-team",
            displayName: null,
            description: null,
            model: "claude-sonnet-4",
            color: "#6366f1",
            ct: TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    // --- #316 + #325: CreateUnitFromTemplateAsync -------------------------

    [Fact]
    public async Task CreateUnitFromTemplateAsync_SendsUnitNameOverrideAndMetadata()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/from-template",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"unit":{"id":"actor-eng","name":"run42-eng","displayName":"Engineering (run 42)","description":"","registeredAt":"2026-04-01T00:00:00Z","status":"Draft","model":"claude-sonnet-4","color":"#336699"},"warnings":["skill 'demo' declares tool 'missing'"],"membersAdded":0}""",
            returnStatusCode: HttpStatusCode.Created,
            validateRequestBody: body =>
            {
                // Kiota serialises optional nullable-string properties even
                // when set. The wire contract for #325 carries `unitName`
                // alongside the existing `name` (template basename). #315
                // rides `model` / `color` on the same body.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("package").GetString().ShouldBe("software-engineering");
                json.GetProperty("name").GetString().ShouldBe("engineering-team");
                json.GetProperty("unitName").GetString().ShouldBe("run42-eng");
                json.GetProperty("displayName").GetString().ShouldBe("Engineering (run 42)");
                json.GetProperty("model").GetString().ShouldBe("claude-sonnet-4");
                json.GetProperty("color").GetString().ShouldBe("#336699");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var response = await client.CreateUnitFromTemplateAsync(
            "software-engineering",
            "engineering-team",
            unitName: "run42-eng",
            displayName: "Engineering (run 42)",
            model: "claude-sonnet-4",
            color: "#336699",
            ct: TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
        response.Unit!.Name.ShouldBe("run42-eng");
        response.Warnings!.Count.ShouldBe(1);
        handler.WasCalled.ShouldBeTrue();
    }

    // --- #453: Unit policy endpoints ---------------------------------------

    [Fact]
    public async Task GetUnitPolicyAsync_CallsCorrectEndpointAndParsesEmptyPolicy()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Get,
            responseBody: "{\"skill\":null,\"model\":null,\"cost\":null,\"executionMode\":null,\"initiative\":null}");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var policy = await client.GetUnitPolicyAsync("eng-team", TestContext.Current.CancellationToken);

        policy.ShouldNotBeNull();
        policy.Skill?.SkillPolicy.ShouldBeNull();
        policy.Model?.ModelPolicy.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetUnitPolicyAsync_PutsMergedPolicyBodyAndDeserialisesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Put,
            responseBody: "{\"skill\":{\"allowed\":[\"github\"],\"blocked\":[\"shell\"]}}",
            validateRequestBody: body =>
            {
                // Kiota's oneOf-composed body serialises as the inner
                // UnitPolicyResponse shape; the wire contract is the same
                // JSON the server reads from the OSS `UnitPolicyResponse`
                // record — verify skill fields round-trip verbatim.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                var skill = json.GetProperty("skill");
                skill.GetProperty("allowed")[0].GetString().ShouldBe("github");
                skill.GetProperty("blocked")[0].GetString().ShouldBe("shell");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var policy = new Cvoya.Spring.Cli.Generated.Models.UnitPolicyResponse
        {
            Skill = new Cvoya.Spring.Cli.Generated.Models.UnitPolicyResponse.UnitPolicyResponse_skill
            {
                SkillPolicy = new Cvoya.Spring.Cli.Generated.Models.SkillPolicy
                {
                    Allowed = new List<string> { "github" },
                    Blocked = new List<string> { "shell" },
                },
            },
        };

        var result = await client.SetUnitPolicyAsync("eng-team", policy, TestContext.Current.CancellationToken);

        // Kiota emits each policy slot as an IComposedTypeWrapper. The
        // request / response round-trip proves both halves are wired: the
        // PUT body carries the skill rules verbatim (validated in the
        // request-body hook above), and the 200 body deserialises into a
        // non-null envelope regardless of which composed-type branch Kiota
        // picks on the read side.
        result.ShouldNotBeNull();
        result.Skill.ShouldNotBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    // --- #454: Humans endpoints --------------------------------------------

    [Fact]
    public async Task ListUnitHumanPermissionsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: "[{\"humanId\":\"alice\",\"permission\":\"Owner\",\"identity\":\"alice@example.com\",\"notifications\":true}]");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var entries = await client.ListUnitHumanPermissionsAsync("eng-team", TestContext.Current.CancellationToken);

        entries.Count.ShouldBe(1);
        entries[0].HumanId.ShouldBe("alice");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetUnitHumanPermissionAsync_PatchesWithContractFields()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/humans/alice/permissions",
            expectedMethod: HttpMethod.Patch,
            responseBody: "{\"humanId\":\"alice\",\"permission\":\"Operator\"}",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("permission").GetString().ShouldBe("operator");
                json.GetProperty("identity").GetString().ShouldBe("alice@example.com");
                json.GetProperty("notifications").GetBoolean().ShouldBeTrue();
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.SetUnitHumanPermissionAsync(
            "eng-team",
            "alice",
            permission: "operator",
            identity: "alice@example.com",
            notifications: true,
            ct: TestContext.Current.CancellationToken);

        result.HumanId.ShouldBe("alice");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveUnitHumanPermissionAsync_DeletesAtCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/humans/alice/permissions",
            expectedMethod: HttpMethod.Delete,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.RemoveUnitHumanPermissionAsync("eng-team", "alice", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    // --- #331: AddUnitMemberAsync ------------------------------------------

    [Fact]
    public async Task AddUnitMemberAsync_PostsUnitSchemeAddress()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/parent-unit/members",
            expectedMethod: HttpMethod.Post,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent,
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                var address = json.GetProperty("memberAddress");
                address.GetProperty("scheme").GetString().ShouldBe("unit");
                address.GetProperty("path").GetString().ShouldBe("child-unit");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.AddUnitMemberAsync(
            "parent-unit", "child-unit", TestContext.Current.CancellationToken);

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
    Action<string>? validateRequestBody = null,
    Action<string>? validateQuery = null) : HttpMessageHandler
{
    public bool WasCalled { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasCalled = true;

        request.RequestUri!.AbsolutePath.ShouldBe(expectedPath);
        request.Method.ShouldBe(expectedMethod);

        if (validateQuery is not null)
        {
            validateQuery(request.RequestUri.Query);
        }

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