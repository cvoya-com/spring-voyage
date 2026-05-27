// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.CommandLine;
using System.Net;
using System.Net.Http;
using System.Text.Json;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

public class CommandParsingTests
{
    private static Option<string> CreateOutputOption()
    {
        return new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            // #1067 — mirror the production binding so tests catch
            // regressions if someone removes Recursive from Program.cs.
            Recursive = true,
        };
    }

    [Fact]
    public void AgentCreate_ParsesNameAndUnitOptions()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        // ADR-0039 §8: identity is server-allocated; --name is the only
        // display surface. --unit is repeatable for multi-parent agents.
        var parseResult = rootCommand.Parse("agent create --name \"My Agent\" --unit engineering");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--name").ShouldBe("My Agent");
        parseResult.GetValue<string[]>("--unit").ShouldBe(new[] { "engineering" });
    }

    [Fact]
    public void AgentCreate_WithoutUnit_ParsesAsTopLevelAgent()
    {
        // ADR-0039 §6: omitting --unit creates a top-level tenant-parented
        // agent that inherits from tenant defaults.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent create --name Foo");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--name").ShouldBe("Foo");
        (parseResult.GetValue<string[]>("--unit") ?? Array.Empty<string>()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AgentCreate_FromPackage_RoutesToPackageInstall()
    {
        var installId = Guid.NewGuid();
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/install",
            expectedMethod: HttpMethod.Post,
            responseBody:
                $$"""{"installId":"{{installId}}","status":"active","packages":[],"startedAt":null,"completedAt":null,"error":null}""",
            returnStatusCode: HttpStatusCode.Created,
            validateRequestBody: body => capturedBody = body);
        var client = new SpringApiClient(new HttpClient(handler), "http://localhost:5000");

        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption, () => client);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent create --name Foo --from-package my-package");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--from-package").ShouldBe("my-package");

        var exitCode = await parseResult.InvokeAsync(
            configuration: null,
            cancellationToken: TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();

        var json = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        var targets = json.GetProperty("targets");
        targets.GetArrayLength().ShouldBe(1);
        targets[0].GetProperty("packageName").GetString().ShouldBe("my-package");
    }

    [Fact]
    public async Task AgentCreate_FromPackageConnector_RoutesToPackageInstallWithConnectorBindings()
    {
        var installId = Guid.NewGuid();
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/install",
            expectedMethod: HttpMethod.Post,
            responseBody:
                $$"""{"installId":"{{installId}}","status":"active","packages":[],"startedAt":null,"completedAt":null,"error":null}""",
            returnStatusCode: HttpStatusCode.Created,
            validateRequestBody: body => capturedBody = body);
        var client = new SpringApiClient(new HttpClient(handler), "http://localhost:5000");

        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption, () => client);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --from-package my-package " +
            "--connector github=binding-123 --connector arxiv");

        parseResult.Errors.ShouldBeEmpty();

        var exitCode = await parseResult.InvokeAsync(
            configuration: null,
            cancellationToken: TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();

        var json = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        var target = json.GetProperty("targets")[0];
        var packageBindings = target
            .GetProperty("connectorBindings")
            .GetProperty("package");

        packageBindings
            .GetProperty("github")
            .GetProperty("config")
            .GetProperty("bindingId")
            .GetString()
            .ShouldBe("binding-123");
        packageBindings
            .GetProperty("arxiv")
            .GetProperty("config")
            .EnumerateObject()
            .Any()
            .ShouldBeFalse();
    }

    [Fact]
    public async Task AgentCreate_FromPackageInput_RoutesToPackageInstallWithInputs()
    {
        var installId = Guid.NewGuid();
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/packages/install",
            expectedMethod: HttpMethod.Post,
            responseBody:
                $$"""{"installId":"{{installId}}","status":"active","packages":[],"startedAt":null,"completedAt":null,"error":null}""",
            returnStatusCode: HttpStatusCode.Created,
            validateRequestBody: body => capturedBody = body);
        var client = new SpringApiClient(new HttpClient(handler), "http://localhost:5000");

        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption, () => client);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --from-package my-package " +
            "--input github_owner=acme --input github_repo=platform");

        parseResult.Errors.ShouldBeEmpty();

        var exitCode = await parseResult.InvokeAsync(
            configuration: null,
            cancellationToken: TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();

        var json = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        var inputs = json
            .GetProperty("targets")[0]
            .GetProperty("inputs");

        inputs.GetProperty("github_owner").GetString().ShouldBe("acme");
        inputs.GetProperty("github_repo").GetString().ShouldBe("platform");
    }

    [Fact]
    public void AgentCreate_ConnectorWithoutFromPackage_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --connector github=binding-123");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == AgentCommand.ConnectorRequiresFromPackageFlagMutexMessage);
    }

    [Fact]
    public void AgentCreate_InputWithoutFromPackage_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --input github_owner=acme");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == AgentCommand.InputRequiresFromPackageFlagMutexMessage);
    }

    [Fact]
    public void AgentCreate_FromPackageWithDefinition_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --from-package my-package --definition {}");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == AgentCommand.FromPackageDefinitionFlagMutexMessage);
    }

    [Fact]
    public void AgentCreate_FromPackageWithInherit_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --from-package my-package --inherit");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == AgentCommand.FromPackageInheritFlagMutexMessage);
    }

    [Fact]
    public void AgentCreate_FromPackageWithRuntime_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --from-package my-package --runtime codex");

        var expectedMessage = AgentCommand.FormatFromPackageExecutionShorthandFlagMutexMessage(
            new[] { "--runtime" });
        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == expectedMessage);
    }

    [Fact]
    public void AgentCreate_FromPackageWithHosting_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name Foo --from-package my-package --hosting persistent");

        var expectedMessage = AgentCommand.FormatFromPackageExecutionShorthandFlagMutexMessage(
            new[] { "--hosting" });
        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == expectedMessage);
    }

    [Fact]
    public void AgentCreate_MissingName_ProducesError()
    {
        // ADR-0039 §8: --name is the only display surface, and it is
        // Required = true. Omitting it must trip the parser before any
        // action runs.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent create --unit engineering");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e => e.Message.Contains("--name"));
    }

    [Fact]
    public void AgentCreate_InheritWithDefinition_RejectedAtParseTime()
    {
        // #1901 / #1902: inherited execution means no explicit create-time
        // definition document should be sent.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name ada --unit aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa " +
            "--inherit --definition {}");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == AgentCommand.InheritDefinitionFlagMutexMessage);
    }

    [Fact]
    public void AgentCreate_InheritWithDefinitionFile_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name ada --unit aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa " +
            "--inherit --definition-file agent.json");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == AgentCommand.InheritDefinitionFlagMutexMessage);
    }

    [Fact]
    public void AgentCreate_InheritWithModel_RejectedAtParseTime()
    {
        // #1902 / ADR-0039 L6: inherited execution and create-time execution
        // shorthands are mutually exclusive.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name ada --unit aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa " +
            "--inherit --model gpt-4o");

        var expectedMessage = AgentCommand.FormatInheritExecutionShorthandFlagMutexMessage(
            new[] { "--model" });
        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == expectedMessage);
    }

    [Fact]
    public void AgentCreate_InheritWithHosting_RejectedAtParseTime()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name ada --unit aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa " +
            "--inherit --hosting persistent");

        var expectedMessage = AgentCommand.FormatInheritExecutionShorthandFlagMutexMessage(
            new[] { "--hosting" });
        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e =>
            e.Message == expectedMessage);
    }

    [Fact]
    public void AgentCreate_ParsesHostingCreateShorthand()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent create --name ada --unit aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa --hosting persistent");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--hosting").ShouldBe("persistent");
    }

    [Fact]
    public void MessageSend_ParsesAddressAndTextArguments()
    {
        var outputOption = CreateOutputOption();
        var messageCommand = MessageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(messageCommand);

        // Canonical address shape per ADR-0036 — `scheme:<32-hex-no-dash>`.
        const string addr = "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
        var parseResult = rootCommand.Parse($"message send {addr} \"Review PR #42\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("address").ShouldBe(addr);
        parseResult.GetValue<string>("text").ShouldBe("Review PR #42");
    }

    [Fact]
    public void MessageSend_ParsesAsFlag()
    {
        // ADR-0062 § 3 / § 6: `--as <human-ref>` pins the explicit
        // 'speaking-as' Hat. The CLI does NOT pre-resolve — it threads
        // the value through to the server's TenantUserHumanResolver,
        // which owns the bound-set validation. Pin the parser shape so
        // a future rename doesn't silently drop the flag.
        var outputOption = CreateOutputOption();
        var messageCommand = MessageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(messageCommand);

        const string addr = "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
        const string hat = "11111111-2222-3333-4444-555555555555";
        var parseResult = rootCommand.Parse(
            $"message send {addr} \"hi\" --as {hat}");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--as").ShouldBe(hat);
    }

    [Fact]
    public void MessageShow_ParsesMessageIdArgument()
    {
        // #1209: `spring message show <id>` surfaces the message body so
        // operators can see *what* was said, not just that something was
        // said. Parse-level guard so a future flag rename doesn't slip
        // past CI.
        var outputOption = CreateOutputOption();
        var messageCommand = MessageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(messageCommand);

        var parseResult = rootCommand.Parse(
            "message show 11111111-2222-3333-4444-555555555555");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("message-id").ShouldBe(
            "11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void UnitCreate_ParsesNameAndMetadataOptions()
    {
        // H2 / #1870 audit: this is not the legacy agent-create positional
        // <id>; it is the current public CreateUnitRequest.Name input.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit create eng-team --display-name \"Engineering Team\" --description \"Builds the product\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("name").ShouldBe("eng-team");
        parseResult.GetValue<string>("--display-name").ShouldBe("Engineering Team");
        parseResult.GetValue<string>("--description").ShouldBe("Builds the product");
    }

    [Fact]
    public void UnitCreate_ParsesAuthMethodCredentialOptions()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit create eng-team --top-level --runtime claude-code --oauth-token token-value");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--runtime").ShouldBe("claude-code");
        parseResult.GetValue<string>("--oauth-token").ShouldBe("token-value");
    }


    [Fact]
    public void OutputOption_AcceptsJson()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("--output json agent list");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    [Fact]
    public void OutputOption_DefaultsToTable()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("table");
    }

    // --- #1067: --output bound recursively, accepts placement after subcommand --

    [Fact]
    public void OutputOption_PlacedAfterSubcommand_ParsesCleanly()
    {
        // #1067: System.CommandLine rejected `unit create demo --output json`
        // because --output was bound to the root only. With Recursive=true
        // the same flag is recognised on every subcommand regardless of
        // placement — the e2e helpers no longer need their `_e2e_split_root_args`
        // hoist.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit create demo --output json");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("json");
        parseResult.GetValue<string>("name").ShouldBe("demo");
    }

    [Fact]
    public void OutputOption_PlacedAfterDeepSubcommand_ParsesCleanly()
    {
        // The recursive binding has to survive nested subcommand chains too
        // — e.g. `unit members list eng-team --output json`. If the option
        // recursion stops at the first level, this would regress.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit members list eng-team --output json");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    [Fact]
    public void OutputOption_ShortAliasAfterSubcommand_ParsesCleanly()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit create demo -o json");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    // --- #320: unit membership management commands ---

    [Fact]
    public void UnitMembersList_ParsesUnitArgument()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("--output json unit members list eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    [Fact]
    public void UnitMembersAdd_ParsesAllOverrideOptions()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members add eng-team --agent ada --model claude-opus-4 --specialty coding --enabled true --execution-mode OnDemand");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<string>("--model").ShouldBe("claude-opus-4");
        parseResult.GetValue<string>("--specialty").ShouldBe("coding");
        parseResult.GetValue<bool?>("--enabled").ShouldBe(true);
        parseResult.GetValue<string>("--execution-mode").ShouldBe("OnDemand");
    }

    [Fact]
    public void UnitMembersAdd_ParsesWithoutAgentOrUnit_LeavesErrorsEmpty()
    {
        // #331 relaxed the parser-level `Required = true` on --agent because
        // `members add` now accepts `--unit` as an alternative. The
        // mutual-exclusion rule ("exactly one of --agent / --unit") is
        // enforced at action time with a readable error message; the parse
        // step itself stays successful so the action can take over and
        // surface the right diagnostic to the user.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit members add eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBeNull();
        parseResult.GetValue<string>("--unit").ShouldBeNull();
    }

    [Fact]
    public void UnitMembersAdd_RejectsInvalidExecutionMode()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members add eng-team --agent ada --execution-mode Invalid");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitMembersConfig_ParsesLikeAdd()
    {
        // `config` is a semantic alias over the same PUT upsert; both share the
        // same flag set so callers can use whichever verb reads better at the
        // shell prompt.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members config eng-team --agent ada --model gpt-4o --enabled false");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<string>("--model").ShouldBe("gpt-4o");
        parseResult.GetValue<bool?>("--enabled").ShouldBe(false);
    }

    [Fact]
    public void UnitMembersRemove_ParsesUnitAndAgent()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit members remove eng-team --agent ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
    }

    // --- #1151: `spring unit members remove --unit <child>` ---------------

    [Fact]
    public void UnitMembersRemove_ParsesUnitOption()
    {
        // Mirror of UnitMembersAdd_ParsesUnitOption (#331). After #1151,
        // `members remove` accepts --unit as an alternative to --agent so a
        // sub-unit edge can be detached through the same verb that creates
        // it.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members remove parent-unit --unit child-unit");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("parent-unit");
        parseResult.GetValue<string>("--unit").ShouldBe("child-unit");
        parseResult.GetValue<string>("--agent").ShouldBeNull();
    }

    [Fact]
    public void UnitMembersRemove_ParsesWithoutAgentOrUnit_LeavesErrorsEmpty()
    {
        // #1151 relaxed the parser-level `Required = true` on --agent
        // because `members remove` now accepts --unit as an alternative.
        // The mutual-exclusion rule ("exactly one of --agent / --unit") is
        // enforced at action time with a readable error message; the parse
        // step itself stays successful so the action can take over and
        // surface the right diagnostic to the user — same shape as the
        // `members add` permissive parse landed by #331.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit members remove eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBeNull();
        parseResult.GetValue<string>("--unit").ShouldBeNull();
    }

    [Fact]
    public void UnitMembersRemove_ParsesAgentAndUnitTogether_ForActionToReject()
    {
        // Parser is intentionally permissive; the action layer enforces the
        // "exactly one of --agent / --unit" rule with a readable error
        // message. See UnitCommand.CreateMembersRemoveCommand for the
        // runtime check introduced by #1151.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members remove parent-unit --agent ada --unit child-unit");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<string>("--unit").ShouldBe("child-unit");
    }

    [Fact]
    public void UnitPurge_ParsesIdAndConfirm()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit purge eng-team --confirm");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("eng-team");
        parseResult.GetValue<bool>("--confirm").ShouldBeTrue();
    }

    [Fact]
    public void UnitPurge_ConfirmDefaultsFalse()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit purge eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("eng-team");
        parseResult.GetValue<bool>("--confirm").ShouldBeFalse();
    }

    [Fact]
    public void AgentPurge_ParsesIdAndConfirm()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent purge ada --confirm");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
        parseResult.GetValue<bool>("--confirm").ShouldBeTrue();
    }

    // --- #315: --model and --color on `spring unit create` ---------------

    [Fact]
    public void UnitCreate_ParsesModelAndColorOptions()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit create eng-team --model claude-sonnet-4-6 --color #6366f1");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("name").ShouldBe("eng-team");
        parseResult.GetValue<string>("--model").ShouldBe("claude-sonnet-4-6");
        parseResult.GetValue<string>("--color").ShouldBe("#6366f1");
    }


    // --- #331: `spring unit members add --unit <child>` -------------------

    [Fact]
    public void UnitMembersAdd_ParsesUnitOption()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members add parent-unit --unit child-unit");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("parent-unit");
        parseResult.GetValue<string>("--unit").ShouldBe("child-unit");
        parseResult.GetValue<string>("--agent").ShouldBeNull();
    }

    [Fact]
    public void UnitMembersAdd_ParsesAgentAndUnitTogether_ForActionToReject()
    {
        // Parser is intentionally permissive; the action layer enforces the
        // "exactly one of --agent / --unit" rule with a readable error
        // message. See UnitCommand.CreateMembersAddCommand for the runtime
        // check introduced by #331.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit members add parent-unit --agent ada --unit child-unit");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<string>("--unit").ShouldBe("child-unit");
    }


    // --- #454: `spring unit humans add|remove|list` -------------------------

    [Fact]
    public void UnitHumansAdd_ParsesObservingGuideInvocationVerbatim()
    {
        // Must keep working verbatim — docs/guide/observing.md §Notifications
        // references this exact invocation. If this test fails, the docs
        // fail alongside it.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit humans add engineering-team savasp --permission owner --notifications slack,email");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering-team");
        parseResult.GetValue<string>("identity").ShouldBe("savasp");
        parseResult.GetValue<string>("--permission").ShouldBe("owner");
        parseResult.GetValue<string>("--notifications").ShouldBe("slack,email");
    }

    [Fact]
    public void UnitHumansAdd_RejectsUnknownPermissionLevel()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit humans add eng-team alice --permission superadmin");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitHumansRemove_ParsesUnitAndIdentity()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit humans remove eng-team alice");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
        parseResult.GetValue<string>("identity").ShouldBe("alice");
    }

    [Fact]
    public void UnitHumansList_ParsesUnitArgumentWithJsonOutput()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("--output json unit humans list eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
        parseResult.GetValue(outputOption).ShouldBe("json");
    }

    // --- #453: `spring unit policy <dim> get|set|clear` for all 5 dimensions -------

    [Theory]
    [InlineData("skill")]
    [InlineData("model")]
    [InlineData("cost")]
    [InlineData("execution-mode")]
    [InlineData("initiative")]
    public void UnitPolicyGet_ParsesEachDimension(string dimension)
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse($"unit policy {dimension} get eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("model")]
    [InlineData("cost")]
    [InlineData("execution-mode")]
    [InlineData("initiative")]
    public void UnitPolicyClear_ParsesEachDimension(string dimension)
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse($"unit policy {dimension} clear eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("eng-team");
    }

    [Fact]
    public void UnitPolicySkillSet_ParsesAllowedAndBlockedLists()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit policy skill set eng-team --allowed github,filesystem --blocked shell");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string[]>("--allowed").ShouldBe(new[] { "github,filesystem" });
        parseResult.GetValue<string[]>("--blocked").ShouldBe(new[] { "shell" });
    }

    [Fact]
    public void UnitPolicyCostSet_ParsesNumericCaps()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit policy cost set eng-team --max-per-invocation 0.5 --max-per-hour 5 --max-per-day 25");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<double?>("--max-per-invocation").ShouldBe(0.5);
        parseResult.GetValue<double?>("--max-per-hour").ShouldBe(5.0);
        parseResult.GetValue<double?>("--max-per-day").ShouldBe(25.0);
    }

    [Fact]
    public void UnitPolicyExecutionModeSet_ParsesForcedValue()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit policy execution-mode set eng-team --forced OnDemand");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--forced").ShouldBe("OnDemand");
    }

    [Fact]
    public void UnitPolicyInitiativeSet_ParsesMaxLevelAndActions()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit policy initiative set eng-team --max-level Proactive --blocked agent.spawn");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--max-level").ShouldBe("Proactive");
        parseResult.GetValue<string[]>("--blocked").ShouldBe(new[] { "agent.spawn" });
    }

    [Fact]
    public void UnitPolicySet_AcceptsYamlFragmentViaFileFlag()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit policy skill set eng-team -f my-policy.yaml");

        parseResult.Errors.ShouldBeEmpty();
        // System.CommandLine stores options under their primary name
        // ("--file" here, with "-f" as an alias); look up by primary name.
        parseResult.GetValue<string>("--file").ShouldBe("my-policy.yaml");
    }

    // --- #1288: `spring thread` verb tree (renamed from `spring conversation`) ---

    [Fact]
    public void ThreadList_ParsesWithNoFilters()
    {
        // #1288: `spring thread list` with no filters should parse without
        // errors. Verifies the verb rename landed and the command tree is wired.
        var outputOption = CreateOutputOption();
        var threadCommand = ThreadCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(threadCommand);

        var parseResult = rootCommand.Parse("thread list");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ThreadList_ParsesAllFilters()
    {
        var outputOption = CreateOutputOption();
        var threadCommand = ThreadCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(threadCommand);

        // Canonical participant address per ADR-0036.
        const string participant = "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
        var parseResult = rootCommand.Parse(
            $"thread list --unit eng-team --agent ada --participant {participant} --limit 25");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--unit").ShouldBe("eng-team");
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<string>("--participant").ShouldBe(participant);
        parseResult.GetValue<int?>("--limit").ShouldBe(25);
    }

    [Fact]
    public void ThreadShow_ParsesIdArgument()
    {
        var outputOption = CreateOutputOption();
        var threadCommand = ThreadCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(threadCommand);

        var parseResult = rootCommand.Parse("thread show t-42");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("t-42");
    }

    [Fact]
    public void ThreadSend_ParsesThreadIdAddressAndText()
    {
        var outputOption = CreateOutputOption();
        var threadCommand = ThreadCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(threadCommand);

        // Canonical address shape per ADR-0036.
        const string addr = "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
        var parseResult = rootCommand.Parse(
            $"thread send --thread t-42 {addr} \"Ship it.\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--thread").ShouldBe("t-42");
        parseResult.GetValue<string>("address").ShouldBe(addr);
        parseResult.GetValue<string>("text").ShouldBe("Ship it.");
    }

    [Fact]
    public void ThreadSend_MissingThreadOption_ProducesError()
    {
        // --thread is required on `thread send`; omitting it must produce a
        // parse-time error before the action can run.
        var outputOption = CreateOutputOption();
        var threadCommand = ThreadCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(threadCommand);

        var parseResult = rootCommand.Parse("thread send agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7 \"Hello\"");

        parseResult.Errors.ShouldNotBeEmpty();
        parseResult.Errors.ShouldContain(e => e.Message.Contains("--thread"));
    }

    // #636: rotate-key + rotate-webhook-secret verb parsing.

    [Fact]
    public void GitHubAppRotateKey_ParsesFromFileAndSlugOptions()
    {
        var outputOption = CreateOutputOption();
        var gitHubAppCommand = GitHubAppCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(gitHubAppCommand);

        var parseResult = rootCommand.Parse(
            "github-app rotate-key --from-file /tmp/key.pem --slug my-app --write-env");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--from-file").ShouldBe("/tmp/key.pem");
        parseResult.GetValue<string>("--slug").ShouldBe("my-app");
        parseResult.GetValue<bool>("--write-env").ShouldBeTrue();
    }

    [Fact]
    public void GitHubAppRotateKey_DryRunFlag_ParsesWithoutFromFile()
    {
        var outputOption = CreateOutputOption();
        var gitHubAppCommand = GitHubAppCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(gitHubAppCommand);

        var parseResult = rootCommand.Parse("github-app rotate-key --dry-run");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--dry-run").ShouldBeTrue();
    }

    [Fact]
    public void GitHubAppRotateWebhookSecret_ParsesFromValueAndWriteSecrets()
    {
        var outputOption = CreateOutputOption();
        var gitHubAppCommand = GitHubAppCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(gitHubAppCommand);

        var parseResult = rootCommand.Parse(
            "github-app rotate-webhook-secret --from-value myNewSecret123456 --write-secrets");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--from-value").ShouldBe("myNewSecret123456");
        parseResult.GetValue<bool>("--write-secrets").ShouldBeTrue();
    }

    [Fact]
    public void GitHubAppRotateWebhookSecret_DryRunFlag_Parses()
    {
        var outputOption = CreateOutputOption();
        var gitHubAppCommand = GitHubAppCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(gitHubAppCommand);

        var parseResult = rootCommand.Parse("github-app rotate-webhook-secret --dry-run --slug acme");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--dry-run").ShouldBeTrue();
        parseResult.GetValue<string>("--slug").ShouldBe("acme");
    }

    // --- #572 / #573: agent list --hosting / --initiative filter flags ---

    [Fact]
    public void AgentList_HostingFlag_ParsesEphemeral()
    {
        // #572: --hosting must accept exactly the two valid values.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list --hosting ephemeral");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--hosting").ShouldBe("ephemeral");
    }

    [Fact]
    public void AgentList_HostingFlag_ParsesPersistent()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list --hosting persistent");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--hosting").ShouldBe("persistent");
    }

    [Fact]
    public void AgentList_HostingFlag_RejectsInvalidValue()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list --hosting transient");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentList_InitiativeFlag_SingleValue_ParsesProactive()
    {
        // #573: --initiative accepts a single value.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list --initiative proactive");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string[]>("--initiative").ShouldBe(new[] { "proactive" });
    }

    [Fact]
    public void AgentList_InitiativeFlag_MultipleValues_ParsesAll()
    {
        // #573: multiple --initiative flags accumulate into a set.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent list --initiative proactive --initiative autonomous");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string[]>("--initiative")
            .ShouldBe(new[] { "proactive", "autonomous" }, ignoreOrder: true);
    }

    [Fact]
    public void AgentList_InitiativeFlag_RejectsInvalidValue()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list --initiative reactive");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentList_AllFilterFlags_ParseCleanly()
    {
        // Both filter flags together — parse without errors.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent list --hosting persistent --initiative autonomous --initiative proactive");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--hosting").ShouldBe("persistent");
        parseResult.GetValue<string[]>("--initiative")
            .ShouldBe(new[] { "autonomous", "proactive" }, ignoreOrder: true);
    }

    // --- #1629 PR6: `agent show` and `unit show` ---------------------

    [Fact]
    public void AgentShow_ParsesGuidArgument()
    {
        // Guid path: the resolver short-circuits and never lists agents.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent show 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id-or-name")
            .ShouldBe("8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7");
    }

    [Fact]
    public void AgentShow_ParsesNameWithUnitContext()
    {
        // Name path with --unit: resolver lists agents, intersects with the
        // unit's memberships, surfaces 0/1/n.
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent show alice --unit engineering");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id-or-name").ShouldBe("alice");
        parseResult.GetValue<string>("--unit").ShouldBe("engineering");
    }

    [Fact]
    public void UnitShow_ParsesGuidArgument()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse(
            "unit show 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id-or-name")
            .ShouldBe("8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7");
    }

    [Fact]
    public void UnitShow_ParsesNameWithParentUnit()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit show backend --unit engineering");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id-or-name").ShouldBe("backend");
        parseResult.GetValue<string>("--unit").ShouldBe("engineering");
    }
}
