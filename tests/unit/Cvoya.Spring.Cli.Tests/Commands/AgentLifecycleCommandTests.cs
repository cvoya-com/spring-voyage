// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parser-level tests for the agent lifecycle subcommands added in
/// PR #2371 — <c>spring agent start | stop | revalidate</c>. Mirrors
/// <see cref="UnitRevalidateCommandTests"/> for the agent surface.
/// </summary>
/// <remarks>
/// The wire integration (Kiota client → API endpoint) is tested at the
/// API layer in <c>AgentLifecycleEndpointTests</c>. These tests pin the
/// CLI parser surface: subcommand registration, required positional
/// arguments, and parsed values.
/// </remarks>
[Collection(ConsoleRedirectionCollection.Name)]
public class AgentLifecycleCommandTests
{
    private const string AgentId = "8c5fab2a8e7e4b8da3e7d18c1a9f0b3c";

    private static Option<string> CreateOutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("revalidate")]
    public void AgentLifecycle_SubcommandRegistered(string verb)
    {
        var agentCommand = AgentCommand.Create(CreateOutputOption());
        agentCommand.Subcommands.ShouldContain(c => c.Name == verb,
            $"`spring agent {verb}` must be registered as a subcommand. " +
            "A rename or typo here silently no-ops the command.");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("revalidate")]
    public void AgentLifecycle_RequiresPositionalId(string verb)
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse($"agent {verb}");

        parseResult.Errors.ShouldNotBeEmpty(
            $"`spring agent {verb}` (no id) must surface a parser error. " +
            "Without the id arg the CLI would silently call the API with an empty path.");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("revalidate")]
    public void AgentLifecycle_AcceptsIdArgument(string verb)
    {
        var outputOption = CreateOutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse($"agent {verb} {AgentId}");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe(AgentId);
    }

    /// <summary>
    /// The pre-#2364 `spring agent validate` static-warnings command
    /// (#2096 / ADR-0041) must continue to coexist with the new
    /// `revalidate` subcommand — they're distinct concepts and a rename
    /// would silently change behaviour for operators.
    /// </summary>
    [Fact]
    public void AgentValidate_StillRegistered_AndDistinctFromRevalidate()
    {
        var agentCommand = AgentCommand.Create(CreateOutputOption());

        agentCommand.Subcommands.ShouldContain(c => c.Name == "validate");
        agentCommand.Subcommands.ShouldContain(c => c.Name == "revalidate");
    }
}
