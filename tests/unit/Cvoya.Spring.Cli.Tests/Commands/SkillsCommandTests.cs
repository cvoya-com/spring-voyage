// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Smoke tests for the <c>spring {agent,unit} skills</c> verb-tree (#2361).
/// Confirms each verb registers under both subjects with the expected
/// argument + option shape and parses without errors. Action-time
/// behaviour (the wire round-trip through <c>SpringApiClient</c>) is
/// covered by the integration suite once a live API host is available;
/// here we lock down only the parser surface, mirroring the
/// <see cref="MemoryCommandTests"/> coverage pattern.
/// </summary>
public class SkillsCommandTests
{
    // ----- Agent verbs --------------------------------------------------

    [Fact]
    public void AgentSkillsList_Parses()
    {
        var parseResult = ParseAgent("agent skills list ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("agent").ShouldBe("ada");
    }

    [Fact]
    public void AgentSkillsAdd_RequiresSkillOption()
    {
        var parseResult = ParseAgent("agent skills add ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentSkillsAdd_ParsesSkillFlag()
    {
        var parseResult = ParseAgent(
            "agent skills add ada --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("agent").ShouldBe("ada");
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void AgentSkillsRemove_RequiresSkillOption()
    {
        var parseResult = ParseAgent("agent skills remove ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentSkillsRemove_ParsesSkillFlag()
    {
        var parseResult = ParseAgent(
            "agent skills remove ada --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void AgentSkillsSet_RequiresSkillsOption()
    {
        var parseResult = ParseAgent("agent skills set ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentSkillsSet_ParsesCommaSeparatedSkills()
    {
        var parseResult = ParseAgent(
            "agent skills set ada --skills a/x,b/y");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skills").ShouldBe("a/x,b/y");
    }

    [Fact]
    public void AgentSkillsSet_AcceptsEmptyStringForClear()
    {
        // The 'set' verb accepts --skills="" as the canonical clear-all form.
        var parseResult = ParseAgent("agent skills set ada --skills \"\"");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skills").ShouldBe(string.Empty);
    }

    // ----- Unit verbs ---------------------------------------------------

    [Fact]
    public void UnitSkillsList_Parses()
    {
        var parseResult = ParseUnit("unit skills list engineering");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering");
    }

    [Fact]
    public void UnitSkillsAdd_RequiresSkillOption()
    {
        var parseResult = ParseUnit("unit skills add engineering");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitSkillsAdd_ParsesSkillFlag()
    {
        var parseResult = ParseUnit(
            "unit skills add engineering --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering");
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void UnitSkillsRemove_ParsesSkillFlag()
    {
        var parseResult = ParseUnit(
            "unit skills remove engineering --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void UnitSkillsSet_ParsesCommaSeparatedSkills()
    {
        var parseResult = ParseUnit(
            "unit skills set engineering --skills a/x,b/y,c/z");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skills").ShouldBe("a/x,b/y,c/z");
    }

    // ----- Output flag --------------------------------------------------

    [Fact]
    public void AgentSkillsList_AcceptsJsonOutput()
    {
        // --output is a root-level recursive option, mirroring how
        // MemoryCommandTests asserts it carries down to leaf verbs.
        var parseResult = ParseAgent("--output json agent skills list ada");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--output").ShouldBe("json");
    }

    [Fact]
    public void UnitSkillsList_AcceptsJsonOutput()
    {
        var parseResult = ParseUnit("--output json unit skills list engineering");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--output").ShouldBe("json");
    }

    // ----- Plumbing -----------------------------------------------------

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
