// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parser-level tests for the <c>spring agent-runtime</c> verb family
/// (#688). Verifies the subcommand tree wires up without regressions;
/// wire-level behaviour against the API surface is covered by the
/// integration tests in <c>Cvoya.Spring.Host.Api.Tests</c>.
/// </summary>
public class AgentRuntimeCommandTests
{
    private static Option<string> CreateOutputOption()
    {
        return new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };
    }

    [Theory]
    [InlineData("agent-runtime list")]
    [InlineData("agent-runtime show claude")]
    [InlineData("agent-runtime install claude")]
    [InlineData("agent-runtime install claude --model claude-opus-4-7 --default-model claude-opus-4-7")]
    [InlineData("agent-runtime uninstall claude --force")]
    [InlineData("agent-runtime models list claude")]
    [InlineData("agent-runtime models set claude claude-opus-4-7,claude-sonnet-4-6")]
    [InlineData("agent-runtime models add claude claude-haiku-4-5")]
    [InlineData("agent-runtime models remove claude claude-haiku-4-5")]
    [InlineData("agent-runtime config set claude defaultModel=claude-opus-4-7")]
    [InlineData("agent-runtime credentials status claude")]
    [InlineData("agent-runtime refresh-models claude")]
    [InlineData("agent-runtime refresh-models claude --credential sk-ant-api-test")]
    [InlineData("agent-runtime refresh-models ollama")]
    public void EveryVerb_ParsesWithoutErrors(string argLine)
    {
        var outputOption = CreateOutputOption();
        var command = AgentRuntimeCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(command);

        var parseResult = root.Parse(argLine);

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Install_WithoutId_FailsToParse()
    {
        var outputOption = CreateOutputOption();
        var command = AgentRuntimeCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(command);

        var parseResult = root.Parse("agent-runtime install");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ModelsSet_WithoutModels_FailsToParse()
    {
        var outputOption = CreateOutputOption();
        var command = AgentRuntimeCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(command);

        var parseResult = root.Parse("agent-runtime models set claude");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void RefreshModels_WithoutId_FailsToParse()
    {
        var outputOption = CreateOutputOption();
        var command = AgentRuntimeCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(command);

        var parseResult = root.Parse("agent-runtime refresh-models");

        parseResult.Errors.ShouldNotBeEmpty();
    }
}