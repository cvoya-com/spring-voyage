// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parse-time + helper tests for the <c>--system-prompt-mode</c> flag on
/// <c>spring agent execution {get,set,clear}</c> and
/// <c>spring unit execution {get,set,clear}</c> (#2693 / #2692 / #2691).
/// Mirrors <see cref="InstructionsSetCommandTests"/> — the full
/// PATCH / PUT round-trip needs a live API; here we cover the flag
/// surface, the value-validation contract, and the tri-state PATCH
/// body shape used by the agent path.
/// </summary>
public class SystemPromptModeCommandTests
{
    // ---- Agent: parse-time -------------------------------------------------

    [Fact]
    public void AgentExecutionSet_AcceptsSystemPromptModeAppend()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent execution set my-agent --system-prompt-mode append");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--system-prompt-mode").ShouldBe("append");
    }

    [Fact]
    public void AgentExecutionSet_AcceptsSystemPromptModeReplace()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent execution set my-agent --system-prompt-mode replace");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--system-prompt-mode").ShouldBe("replace");
    }

    [Fact]
    public void AgentExecutionSet_RejectsUnknownSystemPromptModeValue()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent execution set my-agent --system-prompt-mode shout");
        // AcceptOnlyFromAmong fires at parse time so the operator never gets
        // through to the API call (exit-1 user error contract).
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentExecutionSet_AllowsSystemPromptModeAloneWithoutOtherFlags()
    {
        // #2693: the prompt-shaping slot lives on a separate PATCH endpoint
        // from the execution-block PUT, so the operator must be able to
        // set it without also touching --image / --runtime / --model. The
        // "Nothing to set" gate must include --system-prompt-mode in its
        // any-of check.
        var (root, _) = BuildRoot();
        var result = root.Parse("agent execution set my-agent --system-prompt-mode replace");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--system-prompt-mode").ShouldBe("replace");
    }

    [Fact]
    public void AgentExecutionClear_AcceptsSystemPromptModeBooleanFlag()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent execution clear my-agent --system-prompt-mode");
        result.Errors.ShouldBeEmpty();
        result.GetValue<bool>("--system-prompt-mode").ShouldBeTrue();
    }

    [Fact]
    public void AgentExecutionClear_WithoutSystemPromptMode_DefaultsFalse()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent execution clear my-agent");
        result.Errors.ShouldBeEmpty();
        result.GetValue<bool>("--system-prompt-mode").ShouldBeFalse();
    }

    // ---- Unit: parse-time --------------------------------------------------

    [Fact]
    public void UnitExecutionSet_AcceptsSystemPromptModeAppend()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit execution set my-unit --system-prompt-mode append");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--system-prompt-mode").ShouldBe("append");
    }

    [Fact]
    public void UnitExecutionSet_AcceptsSystemPromptModeReplace()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit execution set my-unit --system-prompt-mode replace");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--system-prompt-mode").ShouldBe("replace");
    }

    [Fact]
    public void UnitExecutionSet_RejectsUnknownSystemPromptModeValue()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit execution set my-unit --system-prompt-mode shout");
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitExecutionSet_AllowsSystemPromptModeAloneWithoutOtherFlags()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit execution set my-unit --system-prompt-mode replace");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--system-prompt-mode").ShouldBe("replace");
    }

    [Fact]
    public void UnitExecutionClear_AcceptsSystemPromptModeBooleanFlag()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit execution clear my-unit --system-prompt-mode");
        result.Errors.ShouldBeEmpty();
        result.GetValue<bool>("--system-prompt-mode").ShouldBeTrue();
    }

    [Fact]
    public void UnitExecutionClear_WithoutSystemPromptMode_DefaultsFalse()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit execution clear my-unit");
        result.Errors.ShouldBeEmpty();
        result.GetValue<bool>("--system-prompt-mode").ShouldBeFalse();
    }

    // ---- Catalogue keys exposed for the AcceptOnlyFromAmong contract ------

    [Fact]
    public void AgentExecution_AdvertisesAppendAndReplaceOnly()
    {
        // Belt-and-braces: the parse-time validator above already rejects
        // unknowns, but documenting the static surface keeps the CLI's
        // accepted set aligned with the server-side NormaliseSystemPromptModeForWire
        // (#2692) — a drift would break round-trip without anyone noticing.
        AgentExecutionCommand.SystemPromptModeKeys.ShouldBe(new[] { "append", "replace" });
    }

    [Fact]
    public void UnitExecution_AdvertisesAppendAndReplaceOnly()
    {
        UnitExecutionCommand.SystemPromptModeKeys.ShouldBe(new[] { "append", "replace" });
    }

    // ---- Wire body shape (tri-state PATCH for the agent path) -------------

    [Fact]
    public void BuildSystemPromptModeBody_NullValue_EmitsExplicitJsonNull()
    {
        // PR E (#2714) made UpdateAgentMetadataRequest.SystemPromptMode
        // tri-state: absent / explicit-null / value. The clear path on
        // `spring agent execution clear --system-prompt-mode` relies on
        // an explicit JSON null on the wire — Kiota would elide it.
        var body = SpringApiClient.BuildSystemPromptModeBody(null);
        body.ShouldBe("{\"systemPromptMode\":null}");
    }

    [Fact]
    public void BuildSystemPromptModeBody_AppendValue_EmitsJsonString()
    {
        var body = SpringApiClient.BuildSystemPromptModeBody("append");
        body.ShouldBe("{\"systemPromptMode\":\"append\"}");
    }

    [Fact]
    public void BuildSystemPromptModeBody_ReplaceValue_EmitsJsonString()
    {
        var body = SpringApiClient.BuildSystemPromptModeBody("replace");
        body.ShouldBe("{\"systemPromptMode\":\"replace\"}");
    }

    [Fact]
    public void BuildSystemPromptModeBody_StringRoundTripsThroughJson()
    {
        // The server-side NormaliseSystemPromptModeForWire rejects anything
        // outside { append, replace }, but the wire body is a verbatim
        // string serialisation — confirm the System.Text.Json escaper
        // handles the same quote / escape patterns that
        // BuildInstructionsBody does, so a future enum literal that
        // contains a special character would not silently break.
        var body = SpringApiClient.BuildSystemPromptModeBody("append");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("systemPromptMode").GetString().ShouldBe("append");
    }

    // ---- Helpers ----------------------------------------------------------

    private static (RootCommand root, Option<string> outputOption) BuildRoot()
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };
        var root = new RootCommand { Options = { outputOption } };
        var agentCommand = new Command("agent");
        agentCommand.Subcommands.Add(AgentExecutionCommand.Create(outputOption));
        root.Subcommands.Add(agentCommand);
        var unitCommand = new Command("unit");
        unitCommand.Subcommands.Add(UnitExecutionCommand.Create(outputOption));
        root.Subcommands.Add(unitCommand);
        return (root, outputOption);
    }
}
