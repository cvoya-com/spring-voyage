// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the client-side filtering behaviour added by #572 / #573 to
/// <c>spring agent list --hosting</c> and <c>spring agent list --initiative</c>.
/// We test the flag surface (parse-time contract) and the filter semantics
/// (the <c>HostingKeys</c> / <c>InitiativeKeys</c> constants and allowed
/// sets) rather than wiring through the full command pipeline, because the
/// pipeline requires a live API server.
/// </summary>
public class AgentListFilterTests
{
    // --- Hosting mode constants (#572) ---

    [Fact]
    public void HostingKeys_ContainsEphemeralAndPersistent()
    {
        AgentCommand.HostingKeys.ShouldBe(new[] { "ephemeral", "persistent" }, ignoreOrder: true);
    }

    // --- Initiative level constants (#573) ---

    [Fact]
    public void InitiativeKeys_ContainsAllFourLevels()
    {
        AgentCommand.InitiativeKeys.ShouldBe(
            new[] { "passive", "attentive", "proactive", "autonomous" },
            ignoreOrder: true);
    }

    // --- Flag acceptance / rejection (parse-time) ---

    [Theory]
    [InlineData("ephemeral")]
    [InlineData("persistent")]
    public void HostingFlag_AcceptsValidValues(string value)
    {
        var (root, outputOption) = BuildRoot();
        var result = root.Parse($"agent list --hosting {value}");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--hosting").ShouldBe(value);
    }

    [Theory]
    [InlineData("passive")]
    [InlineData("attentive")]
    [InlineData("proactive")]
    [InlineData("autonomous")]
    public void InitiativeFlag_AcceptsValidValues(string value)
    {
        var (root, _) = BuildRoot();
        var result = root.Parse($"agent list --initiative {value}");
        result.Errors.ShouldBeEmpty();
        var values = result.GetValue<string[]>("--initiative");
        values.ShouldNotBeNull();
        values.ShouldContain(value);
    }

    [Fact]
    public void InitiativeFlag_MultipleDistinctValues_AllAccepted()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse(
            "agent list --initiative proactive --initiative autonomous");
        result.Errors.ShouldBeEmpty();
        var values = result.GetValue<string[]>("--initiative");
        values.ShouldNotBeNull();
        values.ShouldContain("proactive");
        values.ShouldContain("autonomous");
    }

    [Fact]
    public void HostingFlag_Absent_ReturnsNull()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent list");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--hosting").ShouldBeNull();
    }

    [Fact]
    public void InitiativeFlag_Absent_ReturnsEmptyOrNull()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent list");
        result.Errors.ShouldBeEmpty();
        var values = result.GetValue<string[]>("--initiative");
        (values is null || values.Length == 0).ShouldBeTrue();
    }

    // --- Helper ---

    private static (RootCommand root, Option<string> outputOption) BuildRoot()
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };
        var agentCommand = AgentCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);
        return (root, outputOption);
    }
}