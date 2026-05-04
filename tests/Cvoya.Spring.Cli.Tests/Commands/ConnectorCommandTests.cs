// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Net;
using System.Text.Json;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parser + wire-level tests for the <c>spring connector</c> verb family.
/// Covers both the legacy per-unit binding verbs (<c>catalog</c>,
/// <c>unit-binding</c>, <c>bind</c>, <c>bindings</c>) from #455 / C4,
/// the tenant-bind verbs (<c>list</c>, <c>show</c>, <c>bind-tenant</c>,
/// <c>unbind</c>, <c>config set</c>, <c>credentials status</c>)
/// landed in #689 (renamed from install/uninstall in #1259 / C1.2c), and
/// the platform provision verbs (<c>provision</c>, <c>deprovision</c>)
/// from #1259 / C1.2c.
/// </summary>
public class ConnectorCommandTests
{
    private const string BaseUrl = "http://localhost:5000";

    private static Option<string> CreateOutputOption()
    {
        return new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };
    }

    [Theory]
    [InlineData("connector list")]
    [InlineData("connector show github")]
    [InlineData("connector provision github")]
    [InlineData("connector deprovision github --force")]
    [InlineData("connector bind-tenant github")]
    [InlineData("connector unbind github --force")]
    [InlineData("connector credentials status github")]
    public void ConnectorVerbs_Parse(string argLine)
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse(argLine);

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ConnectorShow_TenantInstall_RequiresPositional()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector show");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ConnectorCatalog_ParsesWithoutArguments()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector catalog");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ConnectorCatalog_ParsesWithJsonOutput()
    {
        // `catalog` is the primary script-consumption path (pipe into jq),
        // so the `--output json` flag must keep parsing through the root
        // command just like every other CLI verb.
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("--output json connector catalog");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    [Fact]
    public void ConnectorShow_ParsesUnitOption()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector unit-binding --unit eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--unit").ShouldBe("eng-team");
    }

    [Fact]
    public void ConnectorShow_WithoutUnit_ReportsMissingRequired()
    {
        // `--unit` is declared Required on the parser so the typical help
        // / error path runs at parse time rather than during the action.
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector unit-binding");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ConnectorBind_ParsesGitHubOptions()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse(
            "connector bind --unit eng-team --type github --owner acme --repo platform --installation-id 12345 --events issues pull_request");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--unit").ShouldBe("eng-team");
        parseResult.GetValue<string>("--type").ShouldBe("github");
        parseResult.GetValue<string>("--owner").ShouldBe("acme");
        parseResult.GetValue<string>("--repo").ShouldBe("platform");
        parseResult.GetValue<string>("--installation-id").ShouldBe("12345");
        parseResult.GetValue<string[]>("--events").ShouldBe(new[] { "issues", "pull_request" });
    }

    [Fact]
    public void ConnectorBind_WithoutUnitOrType_ReportsMissingRequired()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector bind");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ConnectorBindings_ParsesPositionalSlug()
    {
        // `bindings <slugOrId>` is the portal-parity verb for #520 — the
        // slug is positional so the shell form stays ergonomic and matches
        // how the portal deep-links into /connectors/{slug}.
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector bindings github");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("slugOrId").ShouldBe("github");
    }

    [Fact]
    public void ConnectorBindings_WithoutSlug_ReportsMissingRequired()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector bindings");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    // --- wire-level wrappers ------------------------------------------------

    [Fact]
    public async Task ListConnectorsAsync_CallsGenericCatalogEndpoint()
    {
        // Must hit the same endpoint the portal consumes so the CLI stays
        // at parity with what `/connectors` renders on the web side.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """[{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","displayName":"GitHub","description":"Bridge a unit to a GitHub repository","configUrl":"/api/v1/tenant/connectors/github/units/{unitId}/config","actionsBaseUrl":"/api/v1/tenant/connectors/github/actions","configSchemaUrl":"/api/v1/tenant/connectors/github/config-schema"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListConnectorsAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].TypeSlug.ShouldBe("github");
        result[0].DisplayName.ShouldBe("GitHub");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitConnectorAsync_CallsUnitConnectorPointerEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/units/eng-team/connector",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","configUrl":"/api/v1/tenant/connectors/github/units/eng-team/config","actionsBaseUrl":"/api/v1/tenant/connectors/github/actions"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitConnectorAsync("eng-team", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.TypeSlug.ShouldBe("github");
        result.ConfigUrl.ShouldBe("/api/v1/tenant/connectors/github/units/eng-team/config");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitConnectorAsync_ReturnsNullOn404()
    {
        // The server returns 404 when the unit has no active binding; the
        // CLI wrapper normalises this to `null` so callers can surface a
        // clean "no binding" message instead of raising a hard error.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/units/eng-team/connector",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"title":"Not Found","status":404,"detail":"Unit has no active connector binding."}""",
            returnStatusCode: HttpStatusCode.NotFound);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitConnectorAsync("eng-team", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task PutUnitGitHubConfigAsync_SendsOwnerRepoAndEvents()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/units/eng-team/config",
            expectedMethod: HttpMethod.Put,
            responseBody:
                """{"unitId":"eng-team","owner":"acme","repo":"platform","appInstallationId":12345,"events":["issues","pull_request"]}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("owner").GetString().ShouldBe("acme");
                json.GetProperty("repo").GetString().ShouldBe("platform");
                // appInstallationId is now typed as long? in the generated model;
                // the CLI parses the operator-supplied string and sends a JSON number.
                json.GetProperty("appInstallationId").GetInt64().ShouldBe(12345L);
                var events = json.GetProperty("events").EnumerateArray()
                    .Select(e => e.GetString())
                    .ToArray();
                events.ShouldBe(new[] { "issues", "pull_request" });
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.PutUnitGitHubConfigAsync(
            "eng-team",
            owner: "acme",
            repo: "platform",
            appInstallationId: "12345",
            events: new[] { "issues", "pull_request" },
            ct: TestContext.Current.CancellationToken);

        result.Owner.ShouldBe("acme");
        result.Repo.ShouldBe("platform");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task PutUnitGitHubConfigAsync_SendsReviewerWhenProvided()
    {
        // The CLI's --reviewer flag (added alongside the portal's reviewer
        // dropdown) must round-trip onto the wire as a non-null `reviewer`
        // property so the server can persist it on UnitGitHubConfig. We
        // also verify that a blank reviewer is omitted (the API client
        // normalises whitespace to null before serialising).
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/units/eng-team/config",
            expectedMethod: HttpMethod.Put,
            responseBody:
                """{"unitId":"eng-team","owner":"acme","repo":"platform","appInstallationId":12345,"events":["issues"],"reviewer":"alice"}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("reviewer").GetString().ShouldBe("alice");
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.PutUnitGitHubConfigAsync(
            "eng-team",
            owner: "acme",
            repo: "platform",
            appInstallationId: "12345",
            events: new[] { "issues" },
            reviewer: "alice",
            ct: TestContext.Current.CancellationToken);

        result.Reviewer.ShouldBe("alice");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListConnectorBindingsAsync_CallsBulkEndpoint()
    {
        // Single round-trip: the typed wrapper must hit the new bulk
        // endpoint so the portal's N+1 fan-out (per-unit connector lookups
        // introduced in #516) can be retired.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/bindings",
            expectedMethod: HttpMethod.Get,
            // Post-#1629 unitId is a typed Guid on the wire; alpha/beta are
            // the human-readable unit names that ride alongside.
            responseBody:
                """[{"unitId":"a1a1a1a1-1111-1111-1111-aaaaaaaaaaaa","unitName":"alpha","unitDisplayName":"Alpha","typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","configUrl":"/api/v1/tenant/connectors/github/units/alpha/config","actionsBaseUrl":"/api/v1/tenant/connectors/github/actions"},{"unitId":"b2b2b2b2-2222-2222-2222-bbbbbbbbbbbb","unitName":"beta","unitDisplayName":"Beta","typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","configUrl":"/api/v1/tenant/connectors/github/units/beta/config","actionsBaseUrl":"/api/v1/tenant/connectors/github/actions"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListConnectorBindingsAsync("github", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result[0].UnitId.ShouldBe(Guid.Parse("a1a1a1a1-1111-1111-1111-aaaaaaaaaaaa"));
        result[0].UnitName.ShouldBe("alpha");
        result[0].UnitDisplayName.ShouldBe("Alpha");
        result[0].TypeSlug.ShouldBe("github");
        result[0].ConfigUrl.ShouldBe("/api/v1/tenant/connectors/github/units/alpha/config");
        result[1].UnitId.ShouldBe(Guid.Parse("b2b2b2b2-2222-2222-2222-bbbbbbbbbbbb"));
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListConnectorBindingsAsync_ReturnsEmptyArrayWhenNoBindings()
    {
        // Empty-state contract: the server returns [] for a registered
        // connector with no bound units. The CLI must surface an empty list
        // (not null) so `spring connector bindings` prints the parity
        // empty-state message instead of throwing.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/bindings",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListConnectorBindingsAsync("github", TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitGitHubConfigAsync_ReturnsNullOn404()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/units/eng-team/config",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"title":"Not Found","status":404}""",
            returnStatusCode: HttpStatusCode.NotFound);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitGitHubConfigAsync("eng-team", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    // ---- Platform provision / deprovision (#1259 / C1.2c) ----

    [Fact]
    public async Task ProvisionConnectorAsync_CallsPlatformProvisionEndpoint()
    {
        var provisionedAt = DateTimeOffset.UtcNow;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/platform/connectors/github/provision",
            expectedMethod: HttpMethod.Post,
            responseBody: $$"""{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","displayName":"GitHub","description":"Bridge to GitHub","provisionedAt":"{{provisionedAt:O}}","updatedAt":"{{provisionedAt:O}}"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ProvisionConnectorAsync("github", TestContext.Current.CancellationToken);

        result.TypeSlug.ShouldBe("github");
        result.DisplayName.ShouldBe("GitHub");
        result.ProvisionedAt.ShouldNotBe(default);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task DeprovisionConnectorAsync_CallsPlatformDeprovisionEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/platform/connectors/github",
            expectedMethod: HttpMethod.Delete,
            responseBody: string.Empty,
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.DeprovisionConnectorAsync("github", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task BindConnectorAsync_CallsTenantBindEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/bind",
            expectedMethod: HttpMethod.Post,
            responseBody:
                """{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","displayName":"GitHub","description":"Bridge to GitHub","configUrl":"/api/v1/tenant/connectors/github/units/{unitId}/config","actionsBaseUrl":"/api/v1/tenant/connectors/github/actions","configSchemaUrl":"/api/v1/tenant/connectors/github/config-schema","installedAt":"2025-01-01T00:00:00Z","updatedAt":"2025-01-01T00:00:00Z","config":null}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.BindConnectorAsync("github", TestContext.Current.CancellationToken);

        result.TypeSlug.ShouldBe("github");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task UnbindConnectorAsync_CallsDeleteEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github",
            expectedMethod: HttpMethod.Delete,
            responseBody: string.Empty,
            returnStatusCode: HttpStatusCode.NoContent);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        await client.UnbindConnectorAsync("github", TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
    }

    // ---- New verb parse tests (#1259) ----

    [Fact]
    public void Provision_RequiresPositional()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector provision");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Deprovision_RequiresPositional()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector deprovision");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void BindTenant_RequiresPositional()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector bind-tenant");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Unbind_RequiresPositional()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector unbind");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    // ---- `connector config set` (#1619) ----

    [Fact]
    public void ConfigSet_ParsesPositionalArguments()
    {
        // System.CommandLine shells out the inline JSON via its tokeniser
        // (the test passes an unquoted string that's already been split by
        // whitespace), so we feed it argv-style to keep the JSON intact.
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse(
            new[] { "connector", "config", "set", "arxiv", """config={"foo":"bar"}""" });

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("slugOrId").ShouldBe("arxiv");
        parseResult.GetValue<string>("key=value").ShouldBe("""config={"foo":"bar"}""");
    }

    [Fact]
    public void ConfigSet_RequiresBothPositionals()
    {
        var outputOption = CreateOutputOption();
        var connectorCommand = ConnectorCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(connectorCommand);

        var parseResult = rootCommand.Parse("connector config set arxiv");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task UpdateConnectorInstallConfigAsync_PatchesEndpointWithBody()
    {
        // Wire-level happy path for the new helper. The body must round-trip
        // through Kiota's UntypedNode tree onto the wire as a `config`
        // property whose JSON value mirrors the operator-supplied JsonElement.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/arxiv/config",
            expectedMethod: HttpMethod.Patch,
            responseBody:
                """{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb02","typeSlug":"arxiv","displayName":"Arxiv","description":"Arxiv search","configUrl":"/api/v1/tenant/connectors/arxiv/units/{unitId}/config","actionsBaseUrl":"/api/v1/tenant/connectors/arxiv/actions","configSchemaUrl":"/api/v1/tenant/connectors/arxiv/config-schema","installedAt":"2025-01-01T00:00:00Z","updatedAt":"2025-05-01T00:00:00Z","config":{"foo":"bar","n":42,"flag":true,"items":[1,2,3]}}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                var config = json.GetProperty("config");
                config.GetProperty("foo").GetString().ShouldBe("bar");
                config.GetProperty("n").GetInt64().ShouldBe(42L);
                config.GetProperty("flag").GetBoolean().ShouldBeTrue();
                var items = config.GetProperty("items").EnumerateArray()
                    .Select(e => e.GetInt64()).ToArray();
                items.ShouldBe(new[] { 1L, 2L, 3L });
            });

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        using var doc = JsonDocument.Parse("""{"foo":"bar","n":42,"flag":true,"items":[1,2,3]}""");
        var result = await client.UpdateConnectorInstallConfigAsync(
            "arxiv", doc.RootElement.Clone(), TestContext.Current.CancellationToken);

        result.TypeSlug.ShouldBe("arxiv");
        result.UpdatedAt.ShouldNotBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateConnectorInstallConfigAsync_PropagatesNotFound()
    {
        // Unknown connector: the server returns 404, and the wrapper surfaces
        // the Kiota ApiException so callers (the CLI command) can render a
        // clean error and exit non-zero.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/missing/config",
            expectedMethod: HttpMethod.Patch,
            responseBody:
                """{"title":"Not Found","status":404,"detail":"Connector 'missing' is not registered."}""",
            returnStatusCode: HttpStatusCode.NotFound);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        using var doc = JsonDocument.Parse("""{"foo":"bar"}""");
        var ex = await Should.ThrowAsync<Microsoft.Kiota.Abstractions.ApiException>(async () =>
            await client.UpdateConnectorInstallConfigAsync(
                "missing", doc.RootElement.Clone(), TestContext.Current.CancellationToken));

        ex.ResponseStatusCode.ShouldBe(404);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public void JsonElementToUntypedNode_RoundTripsScalarsAndContainers()
    {
        // Direct unit test on the JsonElement → UntypedNode walker — the
        // higher-level wire test exercises the same code path via PATCH, but
        // covering each value-kind branch here keeps regressions on number
        // / null / bool handling cheap to diagnose.
        using var doc = JsonDocument.Parse(
            """{"s":"hello","i":7,"d":1.5,"b":false,"n":null,"a":[true,"x",3]}""");
        var node = SpringApiClient.JsonElementToUntypedNode(doc.RootElement.Clone());

        node.ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedObject>();
        var obj = ((Microsoft.Kiota.Abstractions.Serialization.UntypedObject)node).GetValue();
        obj["s"].ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedString>();
        obj["i"].ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedLong>();
        obj["d"].ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedDouble>();
        obj["b"].ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedBoolean>();
        obj["n"].ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedNull>();
        obj["a"].ShouldBeOfType<Microsoft.Kiota.Abstractions.Serialization.UntypedArray>();
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_HappyPath_ReturnsParsedJsonElement()
    {
        var (parsed, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
            """config={"foo":"bar"}""", TestContext.Current.CancellationToken);

        error.ShouldBeNull();
        parsed.ValueKind.ShouldBe(JsonValueKind.Object);
        parsed.GetProperty("foo").GetString().ShouldBe("bar");
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_EmptyValue_ParsesAsNull()
    {
        // 'config=' (empty) is the documented "clear the payload" form —
        // round-trips as JSON null so the server-side handler stores nothing.
        var (parsed, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
            "config=", TestContext.Current.CancellationToken);

        error.ShouldBeNull();
        parsed.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_MissingEquals_ReturnsError()
    {
        var (_, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
            "config", TestContext.Current.CancellationToken);

        error.ShouldNotBeNull();
        error.ShouldContain("Expected key=value");
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_UnknownKey_ReturnsError()
    {
        var (_, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
            "secret=42", TestContext.Current.CancellationToken);

        error.ShouldNotBeNull();
        error.ShouldContain("Unknown config key");
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_MalformedJson_ReturnsParseError()
    {
        // Issue #1619 acceptance: malformed JSON must surface as a non-null
        // error string so the CLI command can render it to stderr and exit
        // non-zero. The exact JsonException message is BCL-defined; we only
        // assert the user-facing prefix.
        var (_, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
            "config={not-json", TestContext.Current.CancellationToken);

        error.ShouldNotBeNull();
        error.ShouldStartWith("Failed to parse config JSON");
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_ReadsJsonFromAtPath()
    {
        // '@path/to/file.json' is the documented file form — operators with
        // larger config payloads pipe a JSON document into the CLI rather
        // than escaping it through the shell. Round-trips through the disk
        // before the JSON parser runs.
        var path = Path.Combine(Path.GetTempPath(), $"spring-cli-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"a":1,"b":[true,false]}""", TestContext.Current.CancellationToken);
        try
        {
            var (parsed, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
                $"config=@{path}", TestContext.Current.CancellationToken);

            error.ShouldBeNull();
            parsed.GetProperty("a").GetInt32().ShouldBe(1);
            parsed.GetProperty("b").EnumerateArray().Count().ShouldBe(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveConfigArgumentAsync_MissingFile_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spring-cli-missing-{Guid.NewGuid():N}.json");
        var (_, error) = await ConnectorCommand.ResolveConfigArgumentAsync(
            $"config=@{path}", TestContext.Current.CancellationToken);

        error.ShouldNotBeNull();
        error.ShouldContain("Failed to read config file");
    }
}