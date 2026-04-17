// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// PR-C3 (#458): parser coverage for `spring agent clone create/list` so the
/// new CLI verb reaches the same clone endpoints as the portal's Clone action.
/// </summary>
public class AgentCloneCommandTests
{
    private static Option<string> CreateOutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };

    [Fact]
    public void AgentCloneCreate_ParsesAgentAndOptionalName()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);

        var parseResult = root.Parse(
            "agent clone create --agent ada --name ada-clone-1");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<string>("--name").ShouldBe("ada-clone-1");
    }

    [Fact]
    public void AgentCloneCreate_ParsesCloneTypeAndAttachmentMode()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);

        var parseResult = root.Parse(
            "agent clone create --agent ada --clone-type ephemeral-with-memory --attachment-mode attached");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--clone-type").ShouldBe("ephemeral-with-memory");
        parseResult.GetValue<string>("--attachment-mode").ShouldBe("attached");
    }

    [Fact]
    public void AgentCloneCreate_RejectsUnknownCloneType()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);

        var parseResult = root.Parse(
            "agent clone create --agent ada --clone-type permanent");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentCloneCreate_RequiresAgentOption()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);

        var parseResult = root.Parse("agent clone create");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentCloneList_ParsesAgentOption()
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);

        var parseResult = root.Parse("agent clone list --agent ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
    }
}