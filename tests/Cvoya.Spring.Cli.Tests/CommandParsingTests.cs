// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.CommandLine;

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
            DefaultValueFactory = _ => "table"
        };
    }

    [Fact]
    public void AgentCreate_ParsesIdAndNameOptions()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent create my-agent --name \"My Agent\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("my-agent");
        parseResult.GetValue<string>("--name").ShouldBe("My Agent");
    }

    [Fact]
    public void MessageSend_ParsesAddressAndTextArguments()
    {
        var outputOption = CreateOutputOption();
        var messageCommand = MessageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(messageCommand);

        var parseResult = rootCommand.Parse("message send agent://ada \"Review PR #42\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("address").ShouldBe("agent://ada");
        parseResult.GetValue<string>("text").ShouldBe("Review PR #42");
    }

    [Fact]
    public void UnitCreate_ParsesNameAndMetadataOptions()
    {
        // After #117 the CLI mirrors the server CreateUnitRequest contract:
        // a positional `name` (unit address + identifier) plus optional
        // --display-name and --description metadata.
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
    public void ApplyCommand_ParsesFileOption()
    {
        var applyCommand = ApplyCommand.Create();
        var rootCommand = new RootCommand();
        rootCommand.Subcommands.Add(applyCommand);

        var parseResult = rootCommand.Parse("apply -f manifest.yaml");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("-f").ShouldBe("manifest.yaml");
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
    public void UnitMembersAdd_RequiresAgentOption()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit members add eng-team");

        // System.CommandLine surfaces the missing required option as a parse error.
        parseResult.Errors.ShouldNotBeEmpty();
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
}