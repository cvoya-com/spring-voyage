// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Net;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parser + wire-level tests for the <c>spring package</c> and
/// <c>spring template</c> verb families (#395 / PR-PLAT-PKG-1). Keeps
/// parity with the shape established by <see cref="ConnectorCommandTests"/>:
/// parser assertions cover the args / required-flag surface and
/// wire-level assertions cover the typed client wrappers that translate
/// those flags into the HTTP calls.
/// </summary>
public class PackageCommandTests
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

    [Fact]
    public void PackageList_ParsesWithoutArguments()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package list");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageList_ParsesWithJsonOutput()
    {
        // `list` is the primary script-consumption path (pipe into jq) —
        // matches the connector-catalog pattern established in PR-C4.
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("--output json package list");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    [Fact]
    public void PackageShow_ParsesNameArgument()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package show software-engineering");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageShow_WithoutNameArgument_ReportsMissingArgument()
    {
        // The `name` argument is required (the portal routes
        // /packages/[name] similarly require a path segment); leaving it
        // off should surface a parser error rather than falling through
        // to a runtime 400.
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package show");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void TemplateShow_ParsesReference()
    {
        var outputOption = CreateOutputOption();
        var templateCommand = TemplateCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(templateCommand);

        var parseResult = rootCommand.Parse("template show software-engineering/engineering-team");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void TemplateShow_WithoutReference_ReportsMissingArgument()
    {
        var outputOption = CreateOutputOption();
        var templateCommand = TemplateCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(templateCommand);

        var parseResult = rootCommand.Parse("template show");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("software-engineering/engineering-team", "software-engineering", "engineering-team")]
    [InlineData("product-management/retrospective", "product-management", "retrospective")]
    public void TemplateCommand_ParseReference_SplitsOnSingleSlash(
        string reference,
        string expectedPackage,
        string expectedName)
    {
        var (package, name, error) = TemplateCommand.ParseReference(reference);

        package.ShouldBe(expectedPackage);
        name.ShouldBe(expectedName);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash-here")]
    [InlineData("/trailing-empty")]
    [InlineData("leading-empty/")]
    [InlineData("too/many/slashes")]
    public void TemplateCommand_ParseReference_RejectsMalformedInput(string reference)
    {
        var (package, name, error) = TemplateCommand.ParseReference(reference);

        package.ShouldBeNull();
        name.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    // --- wire-level wrappers ------------------------------------------------

    [Fact]
    public async Task ListPackagesAsync_CallsPackagesEndpoint()
    {
        // Must hit the same endpoint the portal consumes so the CLI
        // stays at parity with what /packages renders on the web side.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """[{"name":"software-engineering","description":"Engineering package","unitTemplateCount":1,"agentTemplateCount":3,"skillCount":2,"connectorCount":0,"workflowCount":1}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListPackagesAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("software-engineering");
        result[0].Description.ShouldBe("Engineering package");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetPackageAsync_CallsPackageDetailEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/software-engineering",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"name":"software-engineering","description":null,"unitTemplates":[],"agentTemplates":[],"skills":[],"connectors":[],"workflows":[]}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetPackageAsync(
            "software-engineering", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("software-engineering");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetPackageAsync_ReturnsNullOn404()
    {
        // Matches the connector pointer behaviour — the CLI normalises
        // 404 to null so callers surface a clean "not found" message
        // instead of a hard failure.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/missing",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"title":"Not Found","status":404}""",
            returnStatusCode: HttpStatusCode.NotFound);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetPackageAsync(
            "missing", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitTemplateAsync_CallsTemplateDetailEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/software-engineering/templates/engineering-team",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"package":"software-engineering","name":"engineering-team","path":"software-engineering/units/engineering-team.yaml","yaml":"unit:\n  name: engineering-team\n"}""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitTemplateAsync(
            "software-engineering",
            "engineering-team",
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Package.ShouldBe("software-engineering");
        result.Name.ShouldBe("engineering-team");
        result.Yaml!.ShouldContain("engineering-team");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUnitTemplateAsync_ReturnsNullOn404()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/software-engineering/templates/missing",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"title":"Not Found","status":404}""",
            returnStatusCode: HttpStatusCode.NotFound);

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.GetUnitTemplateAsync(
            "software-engineering",
            "missing",
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListUnitTemplatesAsync_CallsTemplatesEndpoint()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/templates",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """[{"package":"software-engineering","name":"engineering-team","description":"Engineering team","path":"software-engineering/units/engineering-team.yaml"}]""");

        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.ListUnitTemplatesAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Package.ShouldBe("software-engineering");
        result[0].Name.ShouldBe("engineering-team");
        handler.WasCalled.ShouldBeTrue();
    }
}