// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.CommandLine;
using Cvoya.Spring.Cli.Commands;
using FluentAssertions;
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

        parseResult.Errors.Should().BeEmpty();
        parseResult.GetValue<string>("id").Should().Be("my-agent");
        parseResult.GetValue<string>("--name").Should().Be("My Agent");
    }

    [Fact]
    public void MessageSend_ParsesAddressAndTextArguments()
    {
        var outputOption = CreateOutputOption();
        var messageCommand = MessageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(messageCommand);

        var parseResult = rootCommand.Parse("message send agent://ada \"Review PR #42\"");

        parseResult.Errors.Should().BeEmpty();
        parseResult.GetValue<string>("address").Should().Be("agent://ada");
        parseResult.GetValue<string>("text").Should().Be("Review PR #42");
    }

    [Fact]
    public void UnitCreate_ParsesIdAndNameOptions()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit create eng-team --name \"Engineering Team\"");

        parseResult.Errors.Should().BeEmpty();
        parseResult.GetValue<string>("id").Should().Be("eng-team");
        parseResult.GetValue<string>("--name").Should().Be("Engineering Team");
    }

    [Fact]
    public void ApplyCommand_ParsesFileOption()
    {
        var applyCommand = ApplyCommand.Create();
        var rootCommand = new RootCommand();
        rootCommand.Subcommands.Add(applyCommand);

        var parseResult = rootCommand.Parse("apply -f manifest.yaml");

        parseResult.Errors.Should().BeEmpty();
        parseResult.GetValue<string>("-f").Should().Be("manifest.yaml");
    }

    [Fact]
    public void OutputOption_AcceptsJson()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("--output json agent list");

        parseResult.Errors.Should().BeEmpty();
        parseResult.GetValue(outputOption).Should().Be("json");
    }

    [Fact]
    public void OutputOption_DefaultsToTable()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse("agent list");

        parseResult.Errors.Should().BeEmpty();
        parseResult.GetValue(outputOption).Should().Be("table");
    }
}
