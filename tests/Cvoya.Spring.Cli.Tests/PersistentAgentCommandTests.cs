// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parser coverage for the persistent-agent lifecycle verbs added by #396.
/// Mirrors the pattern in <see cref="AgentCloneCommandTests"/> — confirms the
/// command tree parses the expected arguments/options without exercising the
/// HTTP client or the server.
/// </summary>
public class PersistentAgentCommandTests
{
    private static Option<string> CreateOutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };

    private static RootCommand BuildRoot(out Command agentCommand)
    {
        var outputOption = CreateOutputOption();
        agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);
        return root;
    }

    [Fact]
    public void AgentDeploy_ParsesIdWithNoOptions()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent deploy ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
    }

    [Fact]
    public void AgentDeploy_ParsesImageAndReplicasOptions()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse(
            "agent deploy ada --image ghcr.io/cvoya-com/spring-agent:2.1.98 --replicas 1");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
        parseResult.GetValue<string>("--image").ShouldBe("ghcr.io/cvoya-com/spring-agent:2.1.98");
        parseResult.GetValue<int?>("--replicas").ShouldBe(1);
    }

    [Fact]
    public void AgentDeploy_RequiresId()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent deploy");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentUndeploy_ParsesId()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent undeploy ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
    }

    [Fact]
    public void AgentScale_RequiresReplicasOption()
    {
        var root = BuildRoot(out _);

        // Missing --replicas — scale's replicas option is marked Required.
        var parseResult = root.Parse("agent scale ada");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentScale_ParsesReplicas()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent scale ada --replicas 1");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
        parseResult.GetValue<int>("--replicas").ShouldBe(1);
    }

    [Fact]
    public void AgentLogs_ParsesIdWithoutTail()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent logs ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
        parseResult.GetValue<int?>("--tail").ShouldBeNull();
    }

    [Fact]
    public void AgentLogs_ParsesTail()
    {
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent logs ada --tail 50");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<int?>("--tail").ShouldBe(50);
    }

    [Fact]
    public void AgentStatus_StillParsesAfterExtension()
    {
        // The `status` verb existed before #396; its parse surface must not
        // change when the response shape is enriched with deployment info.
        var root = BuildRoot(out _);

        var parseResult = root.Parse("agent status ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("ada");
    }
}