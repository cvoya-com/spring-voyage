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
/// <c>unit-binding</c>, <c>bind</c>, <c>bindings</c>) from #455 / C4 and
/// the tenant-install verbs (<c>list</c>, <c>show</c>, <c>install</c>,
/// <c>uninstall</c>, <c>config set</c>, <c>credentials status</c>)
/// landed in #689.
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
    [InlineData("connector install github")]
    [InlineData("connector uninstall github --force")]
    [InlineData("connector credentials status github")]
    public void ConnectorTenantInstallVerbs_Parse(string argLine)
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
            expectedPath: "/api/v1/connectors",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """[{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","displayName":"GitHub","description":"Bridge a unit to a GitHub repository","configUrl":"/api/v1/connectors/github/units/{unitId}/config","actionsBaseUrl":"/api/v1/connectors/github/actions","configSchemaUrl":"/api/v1/connectors/github/config-schema"}]""");

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
            expectedPath: "/api/v1/units/eng-team/connector",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","configUrl":"/api/v1/connectors/github/units/eng-team/config","actionsBaseUrl":"/api/v1/connectors/github/actions"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitConnectorAsync("eng-team", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.TypeSlug.ShouldBe("github");
        result.ConfigUrl.ShouldBe("/api/v1/connectors/github/units/eng-team/config");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitConnectorAsync_ReturnsNullOn404()
    {
        // The server returns 404 when the unit has no active binding; the
        // CLI wrapper normalises this to `null` so callers can surface a
        // clean "no binding" message instead of raising a hard error.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/units/eng-team/connector",
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
            expectedPath: "/api/v1/connectors/github/units/eng-team/config",
            expectedMethod: HttpMethod.Put,
            responseBody:
                """{"unitId":"eng-team","owner":"acme","repo":"platform","appInstallationId":12345,"events":["issues","pull_request"]}""",
            validateRequestBody: body =>
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                json.GetProperty("owner").GetString().ShouldBe("acme");
                json.GetProperty("repo").GetString().ShouldBe("platform");
                // appInstallationId rides across the wire as an untyped
                // node — the CLI passes it as a string so whatever the
                // operator typed is what the server receives.
                json.GetProperty("appInstallationId").GetString().ShouldBe("12345");
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
    public async Task ListConnectorBindingsAsync_CallsBulkEndpoint()
    {
        // Single round-trip: the typed wrapper must hit the new bulk
        // endpoint so the portal's N+1 fan-out (per-unit connector lookups
        // introduced in #516) can be retired.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/connectors/github/bindings",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """[{"unitId":"alpha","unitName":"alpha","unitDisplayName":"Alpha","typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","configUrl":"/api/v1/connectors/github/units/alpha/config","actionsBaseUrl":"/api/v1/connectors/github/actions"},{"unitId":"beta","unitName":"beta","unitDisplayName":"Beta","typeId":"6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01","typeSlug":"github","configUrl":"/api/v1/connectors/github/units/beta/config","actionsBaseUrl":"/api/v1/connectors/github/actions"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListConnectorBindingsAsync("github", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result[0].UnitId.ShouldBe("alpha");
        result[0].UnitDisplayName.ShouldBe("Alpha");
        result[0].TypeSlug.ShouldBe("github");
        result[0].ConfigUrl.ShouldBe("/api/v1/connectors/github/units/alpha/config");
        result[1].UnitId.ShouldBe("beta");
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
            expectedPath: "/api/v1/connectors/github/bindings",
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
            expectedPath: "/api/v1/connectors/github/units/eng-team/config",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"title":"Not Found","status":404}""",
            returnStatusCode: HttpStatusCode.NotFound);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitGitHubConfigAsync("eng-team", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }
}