// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Smoke tests for the memory command parse paths (#2342). Confirms the
/// agent and unit verb trees register the expected list / get / search
/// subcommands with the right argument + option shape. Action-time
/// behaviour requires a live API server; the integration suite covers
/// the wire end of the contract.
/// </summary>
public class MemoryCommandTests
{
    [Fact]
    public void AgentMemoryList_ParsesWithFilters()
    {
        var parseResult = ParseAgent("agent memory list ada --kind long_term --limit 25 --offset 10");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("agent").ShouldBe("ada");
        parseResult.GetValue<string?>("--kind").ShouldBe("long_term");
        parseResult.GetValue<int?>("--limit").ShouldBe(25);
        parseResult.GetValue<int?>("--offset").ShouldBe(10);
    }

    [Fact]
    public void AgentMemoryList_RejectsUnknownKind()
    {
        var parseResult = ParseAgent("agent memory list ada --kind nope");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentMemoryGet_RequiresIdOption()
    {
        var parseResult = ParseAgent("agent memory get ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentMemoryGet_ParsesWithIdOption()
    {
        var parseResult = ParseAgent(
            "agent memory get ada --id 00000000000000000000000000000001");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("agent").ShouldBe("ada");
        parseResult.GetValue<string>("--id").ShouldBe("00000000000000000000000000000001");
    }

    [Fact]
    public void AgentMemorySearch_RequiresQueryOption()
    {
        var parseResult = ParseAgent("agent memory search ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentMemorySearch_ParsesQueryKindLimit()
    {
        var parseResult = ParseAgent(
            "agent memory search ada --query \"react hooks\" --kind short_term --limit 5");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--query").ShouldBe("react hooks");
        parseResult.GetValue<string?>("--kind").ShouldBe("short_term");
        parseResult.GetValue<int?>("--limit").ShouldBe(5);
    }

    [Fact]
    public void UnitMemoryList_ParsesWithFilters()
    {
        var parseResult = ParseUnit("unit memory list engineering --limit 100");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering");
        parseResult.GetValue<int?>("--limit").ShouldBe(100);
    }

    [Fact]
    public void UnitMemoryGet_ParsesWithIdOption()
    {
        var parseResult = ParseUnit(
            "unit memory get engineering --id 00000000000000000000000000000002");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering");
        parseResult.GetValue<string>("--id").ShouldBe("00000000000000000000000000000002");
    }

    [Fact]
    public void UnitMemorySearch_RequiresQueryOption()
    {
        var parseResult = ParseUnit("unit memory search engineering");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitMemorySearch_ParsesQueryKindLimit()
    {
        var parseResult = ParseUnit(
            "unit memory search engineering --query design --kind long_term --limit 25");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--query").ShouldBe("design");
        parseResult.GetValue<string?>("--kind").ShouldBe("long_term");
        parseResult.GetValue<int?>("--limit").ShouldBe(25);
    }

    private static ParseResult ParseAgent(string commandLine)
    {
        var outputOption = OutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);
        return rootCommand.Parse(commandLine);
    }

    private static ParseResult ParseUnit(string commandLine)
    {
        var outputOption = OutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);
        return rootCommand.Parse(commandLine);
    }

    private static Option<string> OutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };
}
