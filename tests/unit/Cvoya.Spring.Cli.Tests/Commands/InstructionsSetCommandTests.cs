// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.IO;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parse-time + helper tests for the new <c>spring agent set</c> /
/// <c>spring unit set</c> verbs introduced by #2293. The full
/// pipeline requires a live API; here we focus on the flag surface
/// and the <c>@path</c> / empty-string semantics that drive the
/// PATCH body shape.
/// </summary>
public class InstructionsSetCommandTests
{
    // --- Flag-acceptance contract (parse-time) ---

    [Fact]
    public void AgentSet_AcceptsInstructionsLiteralString()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("agent set my-agent --instructions \"You are a backend engineer.\"");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--instructions").ShouldBe("You are a backend engineer.");
    }

    [Fact]
    public void AgentSet_AcceptsInstructionsEmptyStringForClear()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse(new[] { "agent", "set", "my-agent", "--instructions", string.Empty });
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--instructions").ShouldBe(string.Empty);
    }

    [Fact]
    public void UnitSet_AcceptsInstructionsLiteralString()
    {
        var (root, _) = BuildRoot();
        var result = root.Parse("unit set my-unit --instructions \"Be helpful.\"");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string?>("--instructions").ShouldBe("Be helpful.");
    }

    // Note: the `@path` parse-time form is not unit-tested here because
    // System.CommandLine reserves `@` as the response-file token. The
    // resolution path (which strips the `@` prefix and reads the file)
    // is covered by ResolveInstructionsArgument_AtPath_ReadsFileContents
    // below, which tests the helper that the SetAction calls after
    // parsing.

    // --- Argument resolution (the helper) ---

    [Fact]
    public async Task ResolveInstructionsArgument_EmptyString_MapsToNullForClear()
    {
        var result = await AgentCommand.ResolveInstructionsArgumentAsync(
            string.Empty, TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveInstructionsArgument_NullArg_MapsToNull()
    {
        var result = await AgentCommand.ResolveInstructionsArgumentAsync(
            null, TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveInstructionsArgument_LiteralText_PassesThrough()
    {
        var result = await AgentCommand.ResolveInstructionsArgumentAsync(
            "You are helpful.", TestContext.Current.CancellationToken);
        result.ShouldBe("You are helpful.");
    }

    [Fact]
    public async Task ResolveInstructionsArgument_AtPath_ReadsFileContents()
    {
        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, "From file.", TestContext.Current.CancellationToken);
            var result = await AgentCommand.ResolveInstructionsArgumentAsync(
                "@" + temp, TestContext.Current.CancellationToken);
            result.ShouldBe("From file.");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public async Task ResolveInstructionsArgument_AtMissingPath_Throws()
    {
        await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await AgentCommand.ResolveInstructionsArgumentAsync(
                "@/does/not/exist.md", TestContext.Current.CancellationToken);
        });
    }

    // --- Wire body shape (tri-state) ---

    [Fact]
    public void BuildInstructionsBody_NullValue_EmitsExplicitJsonNull()
    {
        var body = SpringApiClient.BuildInstructionsBody(null);
        body.ShouldBe("{\"instructions\":null}");
    }

    [Fact]
    public void BuildInstructionsBody_StringValue_EmitsJsonString()
    {
        var body = SpringApiClient.BuildInstructionsBody("Hello");
        body.ShouldBe("{\"instructions\":\"Hello\"}");
    }

    [Fact]
    public void BuildInstructionsBody_EmbeddedQuotesAndNewlines_RoundTripThroughJson()
    {
        // System.Text.Json's default encoder escapes embedded quotes to
        // " (rather than \") and newlines to \n; both are valid JSON
        // and the server's JsonDocument.Parse round-trips them. Verify by
        // parsing the produced body and checking the value comes back
        // verbatim rather than asserting on a specific escape style.
        var body = SpringApiClient.BuildInstructionsBody("She said \"hi\"\nthen left.");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("instructions").GetString()
            .ShouldBe("She said \"hi\"\nthen left.");
    }

    // --- Helpers ---

    private static (RootCommand root, Option<string> outputOption) BuildRoot()
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };
        var agentCommand = AgentCommand.Create(outputOption);
        var unitCommand = UnitCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(agentCommand);
        root.Subcommands.Add(unitCommand);
        return (root, outputOption);
    }
}
