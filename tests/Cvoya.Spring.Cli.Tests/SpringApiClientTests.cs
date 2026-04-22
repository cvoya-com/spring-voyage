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
                // #744: unitIds is required ≥1 on create.
                var units = json.GetProperty("unitIds");
                units.ValueKind.ShouldBe(JsonValueKind.Array);
                units.GetArrayLength().ShouldBe(1);
                units[0].GetString().ShouldBe("engineering");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.CreateAgentAsync(
            "ada", "Ada", "coder", new[] { "engineering" },
            ct: TestContext.Current.CancellationToken);

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

    // #1000: `spring agent status <id> --output json` crashed when the server
    // modelled the actor status payload as `JsonElement?`. The OpenAPI
    // lowering produced an empty-schema oneOf that Kiota generated as a
    // composed type with ambiguous discriminators; serialising it back out
    // via OutputFormatter.FormatJson threw
    // `System.InvalidOperationException: '}' is invalid following a property name`.
    // Switching the wire type to `string?` (same convention as
    // CreateAgentRequest.DefinitionJson) keeps the client flat — this test
    // locks the round-trip for that shape.
    [Fact]
    public async Task GetAgentStatusAsync_RoundTripsThroughFormatJson()
    {
        var responseBody = """
        {
          "agent": {
            "id": "agent-id",
            "name": "ada",
            "displayName": "Ada",
            "description": "",
            "role": null,
            "registeredAt": "2026-04-01T00:00:00Z",
            "model": null,
            "specialty": null,
            "enabled": true,
            "executionMode": "Auto",
            "parentUnit": "eng-team"
          },
          "status": "{\"Status\":\"Idle\",\"ActiveConversationId\":null,\"PendingConversationCount\":0}",
          "deployment": null
        }
        """;

        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetAgentStatusAsync("ada", TestContext.Current.CancellationToken);

        result.Agent!.Name.ShouldBe("ada");
        result.Status.ShouldNotBeNullOrEmpty();
        // The status field is a JSON document serialised as a string — callers
        // that want to read it re-parse with JsonDocument.Parse.
        using var statusDoc = System.Text.Json.JsonDocument.Parse(result.Status!);
        statusDoc.RootElement.GetProperty("Status").GetString().ShouldBe("Idle");

        // The --output json code path (OutputFormatter.FormatJson) must not
        // throw — that's the regression #1000 tracked.
        var formatted = Cvoya.Spring.Cli.Output.OutputFormatter.FormatJson(result);
        formatted.ShouldContain("\"status\":");
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

    // --- T-08 / #950: RevalidateUnitAsync --------------------------------

    [Fact]
    public async Task RevalidateUnitAsync_PostsRevalidateEndpoint_ParsesUnitResponse()
    {
        // Typical happy path: server returns 202 Accepted with the unit
        // flipped to Validating + a fresh workflow instance id.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/revalidate",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"id":"actor-eng","name":"eng-team","displayName":"eng-team","description":"","registeredAt":"2026-04-01T00:00:00Z","status":"Validating","model":"claude-sonnet-4","color":"#6366f1","tool":"claude-code","lastValidationError":null,"lastValidationRunId":"run-42"}""",
            returnStatusCode: HttpStatusCode.Accepted);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.RevalidateUnitAsync(
            "eng-team", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("eng-team");
        result.Status.ShouldBe(Cvoya.Spring.Cli.Generated.Models.UnitStatus.Validating);
        result.LastValidationRunId.ShouldBe("run-42");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RevalidateUnitAsync_ConflictResponseSurfacesAsApiException()
    {
        // 409 = the unit is in a state where revalidation isn't allowed.
        // The CLI `revalidate` verb catches this and exits 2 (usage error),
        // so the wrapper must surface it as an ApiException the caller can
        // branch on via ResponseStatusCode.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/revalidate",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"type":"about:blank","title":"Invalid state","detail":"Unit 'eng-team' is Running; revalidation is only allowed from Error or Stopped.","status":409,"code":"InvalidState","currentStatus":"Running"}""",
            returnStatusCode: HttpStatusCode.Conflict);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var ex = await Should.ThrowAsync<Microsoft.Kiota.Abstractions.ApiException>(async () =>
            await client.RevalidateUnitAsync(
                "eng-team", TestContext.Current.CancellationToken));

        ex.ResponseStatusCode.ShouldBe(409);
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

    // --- Unit policy endpoints ---------------------------------------------

    [Fact]
    public async Task GetUnitPolicyAsync_CallsCorrectEndpointAndParsesEmptyPolicy()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Get,
            responseBody: "{\"skill\":null,\"model\":null,\"cost\":null,\"executionMode\":null,\"initiative\":null,\"labelRouting\":null}");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var policy = await client.GetUnitPolicyAsync("eng-team", TestContext.Current.CancellationToken);

        policy.ShouldNotBeNull();
        policy.Skill.ShouldBeNull();
        policy.Model.ShouldBeNull();
        policy.Cost.ShouldBeNull();
        policy.ExecutionMode.ShouldBeNull();
        policy.Initiative.ShouldBeNull();
        policy.LabelRouting.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitPolicyAsync_PopulatesSlotFieldsFromWireResponse()
    {
        // Regression for #999: Kiota's oneOf [null, T] generator produced a
        // composed-type wrapper whose CreateFromDiscriminatorValue read an
        // empty-string discriminator and never populated the inner sub-record
        // — so a populated `skill` slot came back with Allowed/Blocked both
        // null. The raw HTTP path must surface the fields verbatim.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Get,
            responseBody:
                "{\"skill\":{\"allowed\":[\"github\",\"filesystem\"],\"blocked\":[\"shell\"]}," +
                "\"model\":null,\"cost\":null,\"executionMode\":null,\"initiative\":null,\"labelRouting\":null}");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var policy = await client.GetUnitPolicyAsync("eng-team", TestContext.Current.CancellationToken);

        policy.Skill.ShouldNotBeNull();
        policy.Skill!.Allowed.ShouldBe(new[] { "github", "filesystem" });
        policy.Skill.Blocked.ShouldBe(new[] { "shell" });
    }

    [Fact]
    public async Task SetUnitPolicyAsync_PutsFullPolicyBodyAndDeserialisesResponse()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Put,
            responseBody: "{\"skill\":{\"allowed\":[\"github\"],\"blocked\":[\"shell\"]}}",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                var skill = json.GetProperty("skill");
                skill.GetProperty("allowed")[0].GetString().ShouldBe("github");
                skill.GetProperty("blocked")[0].GetString().ShouldBe("shell");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var policy = new UnitPolicyWire
        {
            Skill = new SkillPolicyWire
            {
                Allowed = new List<string> { "github" },
                Blocked = new List<string> { "shell" },
            },
        };

        var result = await client.SetUnitPolicyAsync("eng-team", policy, TestContext.Current.CancellationToken);

        // The response round-trips cleanly — both the request body carries
        // the skill rules verbatim (validated in the request-body hook) and
        // the 200 body deserialises into a fully-populated UnitPolicyWire
        // with the skill sub-record readable (regression for #999 where the
        // Kiota composed-type wrapper dropped the inner fields).
        result.ShouldNotBeNull();
        result.Skill.ShouldNotBeNull();
        result.Skill!.Allowed.ShouldBe(new[] { "github" });
        result.Skill.Blocked.ShouldBe(new[] { "shell" });
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetUnitPolicyAsync_RoundTripAfterGet_SurvivesSubsequentSet()
    {
        // Regression for #999: after the first set, a second set on another
        // dimension does a GET first, then a PUT with the merged result.
        // With the Kiota wrappers, the second PUT crashed with
        // `'}' is invalid following a property name` because the composed
        // wrapper serialized as an empty object stuck in property-name mode.
        // The plain-DTO path must round-trip cleanly end-to-end.
        var getHandler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Get,
            responseBody: "{\"skill\":{\"allowed\":[\"github\"],\"blocked\":null}}");

        var getClient = new SpringApiClient(new HttpClient(getHandler), BaseUrl);
        var current = await getClient.GetUnitPolicyAsync("eng-team", TestContext.Current.CancellationToken);

        current.Model = new ModelPolicyWire { Allowed = new List<string> { "gpt-4o-mini" } };

        var putHandler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/policy",
            expectedMethod: HttpMethod.Put,
            responseBody:
                "{\"skill\":{\"allowed\":[\"github\"]},\"model\":{\"allowed\":[\"gpt-4o-mini\"]}}",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("skill").GetProperty("allowed")[0].GetString().ShouldBe("github");
                json.GetProperty("model").GetProperty("allowed")[0].GetString().ShouldBe("gpt-4o-mini");
            });
        var putClient = new SpringApiClient(new HttpClient(putHandler), BaseUrl);

        var stored = await putClient.SetUnitPolicyAsync("eng-team", current, TestContext.Current.CancellationToken);

        stored.Skill!.Allowed.ShouldBe(new[] { "github" });
        stored.Model!.Allowed.ShouldBe(new[] { "gpt-4o-mini" });
    }

    // --- Humans endpoints --------------------------------------------------

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

    // --- Analytics: costs, throughput, waits -------------------------------

    [Fact]
    public async Task GetTenantCostAsync_CallsCorrectEndpoint_AndHonoursWindow()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/costs/tenant",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"totalCost":12.34,"totalInputTokens":100,"totalOutputTokens":50,"recordCount":3,"workCost":10.00,"initiativeCost":2.34,"from":"2026-04-01T00:00:00Z","to":"2026-04-16T00:00:00Z"}""",
            validateQuery: query =>
            {
                // `from` and `to` travel as ISO 8601 timestamps per the
                // Kiota query parameter bindings (`DateTimeOffset? From`).
                query.ShouldContain("from=");
                query.ShouldContain("to=");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetTenantCostAsync(
            from: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
            to: DateTimeOffset.Parse("2026-04-16T00:00:00Z"),
            ct: TestContext.Current.CancellationToken);

        result.TotalCost.ShouldBe(12.34);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitCostAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/costs/units/eng-team",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"totalCost":0.0,"totalInputTokens":0,"totalOutputTokens":0,"recordCount":0,"workCost":0.0,"initiativeCost":0.0,"from":"2026-04-01T00:00:00Z","to":"2026-04-16T00:00:00Z"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitCostAsync("eng-team", ct: TestContext.Current.CancellationToken);

        result.TotalCost.ShouldBe(0.0);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAgentCostAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/costs/agents/ada",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"totalCost":1.25,"totalInputTokens":1,"totalOutputTokens":1,"recordCount":1,"workCost":1.25,"initiativeCost":0.0,"from":"2026-04-01T00:00:00Z","to":"2026-04-16T00:00:00Z"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetAgentCostAsync("ada", ct: TestContext.Current.CancellationToken);

        result.TotalCost.ShouldBe(1.25);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetThroughputAsync_CallsCorrectEndpoint_WithSourceFilter()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/analytics/throughput",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"entries":[{"source":"agent://ada","messagesReceived":3,"messagesSent":2,"turns":1,"toolCalls":4}],"from":"2026-04-01T00:00:00Z","to":"2026-04-16T00:00:00Z"}""",
            validateQuery: query =>
            {
                // Substring filter for cross-agent rollups — `agent://`
                // matches every agent, `agent://ada` scopes to ada.
                query.ShouldContain("source=agent%3A%2F%2Fada");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetThroughputAsync(
            source: "agent://ada",
            ct: TestContext.Current.CancellationToken);

        result.Entries.ShouldNotBeNull();
        result.Entries!.Count.ShouldBe(1);
        result.Entries![0].Source.ShouldBe("agent://ada");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetThroughputAsync_WithoutSource_RetrievesCrossAgentRollup()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/analytics/throughput",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"entries":[{"source":"agent://ada","messagesReceived":1,"messagesSent":1,"turns":1,"toolCalls":0},{"source":"agent://grace","messagesReceived":2,"messagesSent":0,"turns":0,"toolCalls":3}],"from":"2026-04-01T00:00:00Z","to":"2026-04-16T00:00:00Z"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetThroughputAsync(ct: TestContext.Current.CancellationToken);

        result.Entries.ShouldNotBeNull();
        result.Entries!.Count.ShouldBe(2);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetWaitTimesAsync_CallsCorrectEndpoint_AndDeserializesEntries()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/analytics/waits",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"entries":[{"source":"agent://ada","idleSeconds":0.0,"busySeconds":0.0,"waitingForHumanSeconds":0.0,"stateTransitions":7}],"from":"2026-04-01T00:00:00Z","to":"2026-04-16T00:00:00Z"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetWaitTimesAsync(ct: TestContext.Current.CancellationToken);

        result.Entries.ShouldNotBeNull();
        result.Entries!.Count.ShouldBe(1);
        result.Entries![0].Source.ShouldBe("agent://ada");
        handler.WasCalled.ShouldBeTrue();
    }

    // --- PR-C3 / #459: set-budget across tenant / unit / agent scopes --------

    [Fact]
    public async Task SetTenantBudgetAsync_PutsEndpointAndReturnsDailyBudget()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/budget",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"dailyBudget":50.0}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("dailyBudget").GetDouble().ShouldBe(50.0);
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.SetTenantBudgetAsync(50m, ct: TestContext.Current.CancellationToken);

        result.DailyBudget.ShouldBe(50.0);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetUnitBudgetAsync_PutsEndpointAndReturnsDailyBudget()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/budget",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"dailyBudget":20.0}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("dailyBudget").GetDouble().ShouldBe(20.0);
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.SetUnitBudgetAsync(
            "eng-team", 20m, TestContext.Current.CancellationToken);

        // Round-trip: what the CLI reads back must match what `spring cost
        // budget` reads (both hit the same GET endpoint for the same key).
        result.DailyBudget.ShouldBe(20.0);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetAgentBudgetAsync_PutsEndpointAndReturnsDailyBudget()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada/budget",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"dailyBudget":5.0}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("dailyBudget").GetDouble().ShouldBe(5.0);
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.SetAgentBudgetAsync(
            "ada", 5m, TestContext.Current.CancellationToken);

        result.DailyBudget.ShouldBe(5.0);
        handler.WasCalled.ShouldBeTrue();
    }

    // --- PR-C3 / #458: agent clone create / list --------------------------

    [Fact]
    public async Task CreateCloneAsync_PostsDefaultsMatchingPortalAction()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada/clones",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"cloneId":"{{Guid.NewGuid()}}","parentAgentId":"ada","cloneType":"ephemeral-no-memory","attachmentMode":"detached","status":"provisioning","createdAt":"2026-04-16T00:00:00Z"}""",
            returnStatusCode: System.Net.HttpStatusCode.Accepted,
            validateRequestBody: body =>
            {
                // Defaults must match the portal's Create Clone form so a
                // clone created via CLI carries the same identity / config
                // as one created by the UI.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("cloneType").GetString().ShouldBe("ephemeral-no-memory");
                json.GetProperty("attachmentMode").GetString().ShouldBe("detached");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.CreateCloneAsync(
            "ada", ct: TestContext.Current.CancellationToken);

        result.ParentAgentId.ShouldBe("ada");
        result.CloneType.ShouldBe(Cvoya.Spring.Cli.Generated.Models.CloningPolicy.EphemeralNoMemory);
        result.AttachmentMode.ShouldBe(Cvoya.Spring.Cli.Generated.Models.AttachmentMode.Detached);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateCloneAsync_ForwardsExplicitCloneTypeAndAttachment()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada/clones",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"cloneId":"{{Guid.NewGuid()}}","parentAgentId":"ada","cloneType":"ephemeral-with-memory","attachmentMode":"attached","status":"provisioning","createdAt":"2026-04-16T00:00:00Z"}""",
            returnStatusCode: System.Net.HttpStatusCode.Accepted,
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("cloneType").GetString().ShouldBe("ephemeral-with-memory");
                json.GetProperty("attachmentMode").GetString().ShouldBe("attached");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.CreateCloneAsync(
            "ada",
            cloneType: Cvoya.Spring.Cli.Generated.Models.CloningPolicy.EphemeralWithMemory,
            attachmentMode: Cvoya.Spring.Cli.Generated.Models.AttachmentMode.Attached,
            ct: TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListClonesAsync_CallsCorrectEndpoint_AndDeserializesList()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada/clones",
            expectedMethod: HttpMethod.Get,
            responseBody: """[{"cloneId":"11111111-1111-1111-1111-111111111111","parentAgentId":"ada","cloneType":"ephemeral-no-memory","attachmentMode":"detached","status":"active","createdAt":"2026-04-16T00:00:00Z"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListClonesAsync(
            "ada", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].ParentAgentId.ShouldBe("ada");
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

    // --- #432: secret wrappers (unit + tenant + platform scopes) ---

    [Fact]
    public async Task ListUnitSecretsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/secrets",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"secrets":[{"name":"openai-api-key","scope":"Unit","createdAt":"2026-04-01T00:00:00Z"}]}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListUnitSecretsAsync("eng-team", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("openai-api-key");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListTenantSecretsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/secrets",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"secrets":[]}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListTenantSecretsAsync(TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListPlatformSecretsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/platform/secrets",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"secrets":[{"name":"system-webhook-signing-key","scope":"Platform","createdAt":"2026-04-01T00:00:00Z"}]}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListPlatformSecretsAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("system-webhook-signing-key");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateUnitSecretAsync_SendsPlaintextBody()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/secrets",
            expectedMethod: HttpMethod.Post,
            responseBody:
                """{"name":"openai-api-key","scope":"Unit","createdAt":"2026-04-01T00:00:00Z"}""",
            validateRequestBody: body =>
            {
                // Kiota's JSON writer omits null properties entirely (drops
                // them rather than emitting `"field":null`), so the presence
                // check is "externalStoreKey must NOT be in the body" when
                // we're doing a pass-through write.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("name").GetString().ShouldBe("openai-api-key");
                json.GetProperty("value").GetString().ShouldBe("sk-live-...");
                json.TryGetProperty("externalStoreKey", out _).ShouldBeFalse();
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.CreateUnitSecretAsync(
            "eng-team",
            "openai-api-key",
            value: "sk-live-...",
            externalStoreKey: null,
            TestContext.Current.CancellationToken);

        result.Name.ShouldBe("openai-api-key");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateTenantSecretAsync_SendsExternalReferenceBody()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/secrets",
            expectedMethod: HttpMethod.Post,
            responseBody:
                """{"name":"observability-token","scope":"Tenant","createdAt":"2026-04-01T00:00:00Z"}""",
            validateRequestBody: body =>
            {
                // Null props are omitted by Kiota — verifying `value` is
                // absent is the correct assertion for an external-ref write.
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("name").GetString().ShouldBe("observability-token");
                json.GetProperty("externalStoreKey").GetString().ShouldBe("kv://prod/obs");
                json.TryGetProperty("value", out _).ShouldBeFalse();
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.CreateTenantSecretAsync(
            "observability-token",
            value: null,
            externalStoreKey: "kv://prod/obs",
            TestContext.Current.CancellationToken);

        result.Name.ShouldBe("observability-token");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RotateUnitSecretAsync_PutsPlaintextAndReturnsNewVersion()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/secrets/openai-api-key",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"name":"openai-api-key","scope":"Unit","version":2}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("value").GetString().ShouldBe("sk-live-NEW...");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.RotateUnitSecretAsync(
            "eng-team",
            "openai-api-key",
            value: "sk-live-NEW...",
            externalStoreKey: null,
            TestContext.Current.CancellationToken);

        result.Name.ShouldBe("openai-api-key");
        // Version is modelled as UntypedNode because Kiota drops int32 format
        // for integer/string unions — unpack via the shared helper.
        Cvoya.Spring.Cli.KiotaConversions.ToInt(result.Version).ShouldBe(2);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListUnitSecretVersionsAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/secrets/openai-api-key/versions",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"name":"openai-api-key","scope":"Unit","versions":[{"version":1,"origin":"PlatformOwned","createdAt":"2026-04-01T00:00:00Z","isCurrent":false},{"version":2,"origin":"PlatformOwned","createdAt":"2026-04-02T00:00:00Z","isCurrent":true}]}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListUnitSecretVersionsAsync(
            "eng-team", "openai-api-key", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("openai-api-key");
        result.Versions!.Count.ShouldBe(2);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task PruneUnitSecretAsync_SendsKeepQueryParam()
    {
        string? capturedQuery = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/secrets/openai-api-key/prune",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"name":"openai-api-key","scope":"Unit","keep":2,"pruned":3}""",
            validateQuery: q => capturedQuery = q);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.PruneUnitSecretAsync(
            "eng-team", "openai-api-key", 2, TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("keep=2");
        Cvoya.Spring.Cli.KiotaConversions.ToInt(result.Keep).ShouldBe(2);
        Cvoya.Spring.Cli.KiotaConversions.ToInt(result.Pruned).ShouldBe(3);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteUnitSecretAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/secrets/openai-api-key",
            expectedMethod: HttpMethod.Delete,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.DeleteUnitSecretAsync(
            "eng-team", "openai-api-key", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task DeletePlatformSecretAsync_CallsCorrectEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/platform/secrets/system-webhook-signing-key",
            expectedMethod: HttpMethod.Delete,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.DeletePlatformSecretAsync(
            "system-webhook-signing-key", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    // #1021 — regression coverage for the Kiota oneOf(null, $ref) request-body
    // bug. Endpoints declared the request body as `TRequest? request` (nullable
    // reference type), which OpenAPI 3.1 lowered to oneOf: [null, $ref]. Kiota
    // generated an IComposedTypeWrapper (FooPutRequestBody) whose Serialize
    // writes a property with no name, producing `{"": {...}}` on the wire that
    // Utf8JsonWriter.ValidateEnd refused ("'}' is invalid following a property
    // name"). Dropping `?` on the endpoint param flattens the OpenAPI to a
    // plain $ref and Kiota emits the body as a top-level object. Each
    // endpoint's PUT body must be a plain JSON object whose fields are the
    // TRequest's properties — not a one-key wrapper.
    [Fact]
    public async Task SetUnitBoundaryAsync_SerialisesBodyAsPlainObject()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/boundary",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"opacities":[],"projections":[],"syntheses":[]}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                // Plain UnitBoundaryResponse shape — not a composed wrapper with
                // an empty-string key. If the old Member1 wrapper were still in
                // place the body would be `{"": {...}}` and this check would
                // fail on the projections property not existing at the root.
                json.ValueKind.ShouldBe(JsonValueKind.Object);
                json.TryGetProperty("", out _).ShouldBeFalse();
                json.TryGetProperty("projections", out var projections).ShouldBeTrue();
                projections.ValueKind.ShouldBe(JsonValueKind.Array);
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var boundary = new Cvoya.Spring.Cli.Generated.Models.UnitBoundaryResponse
        {
            Projections = new List<Cvoya.Spring.Cli.Generated.Models.BoundaryProjectionRuleDto>(),
        };
        await client.SetUnitBoundaryAsync(
            "eng-team", boundary, TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    // --- #1038 — `spring conversation close <id>` API path ---

    [Fact]
    public async Task CloseConversationAsync_PostsCloseEndpointWithReason()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/conversations/c-1/close",
            expectedMethod: HttpMethod.Post,
            responseBody: """{"summary":{"id":"c-1","participants":["agent://ada"],"status":"closed","createdAt":"2026-04-01T00:00:00Z","lastActivity":"2026-04-01T00:00:01Z","messageCount":2,"lastSpeaker":"agent://ada","lastSummary":"Conversation closed"},"events":[]}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("reason").GetString().ShouldBe("operator request");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.CloseConversationAsync(
            "c-1", reason: "operator request", ct: TestContext.Current.CancellationToken);

        result.Summary!.Id.ShouldBe("c-1");
        result.Summary.Status.ShouldBe("closed");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetAgentCloningPolicyAsync_SerialisesBodyAsPlainObject()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/agents/ada/cloning-policy",
            expectedMethod: HttpMethod.Put,
            responseBody: """{"enabled":true}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.ValueKind.ShouldBe(JsonValueKind.Object);
                // The real field (whatever the server defines) must live at the
                // root, not under an empty-string discriminator key.
                json.TryGetProperty("", out _).ShouldBeFalse();
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var policy = new Cvoya.Spring.Cli.Generated.Models.AgentCloningPolicyResponse();
        await client.SetAgentCloningPolicyAsync(
            "ada", policy, TestContext.Current.CancellationToken);

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