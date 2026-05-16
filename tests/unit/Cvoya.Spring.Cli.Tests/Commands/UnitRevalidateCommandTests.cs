// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.IO;
using System.Net;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// T-08 / #950: parser-level tests for the new <c>spring unit revalidate</c>
/// verb and the <c>--no-wait</c> flag added to <c>spring unit create</c>.
/// These tests exercise the System.CommandLine surface so we catch
/// casing / flag-name regressions without spinning up an API.
///
/// The wait-loop behaviour itself is tested in
/// <see cref="ArtefactValidationWaitLoopTests"/>; the action-level wire
/// integration is owned by the existing <c>SpringApiClientTests</c> (the
/// Kiota-wrapper layer that <c>RevalidateUnitAsync</c> goes through).
/// </summary>
[Collection(ConsoleRedirectionCollection.Name)]
public class UnitRevalidateCommandTests
{
    private static Option<string> CreateOutputOption()
    {
        return new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };
    }

    [Fact]
    public void UnitRevalidate_IsRegisteredAsASubcommand()
    {
        // Regression: forgetting to add the subcommand means the CLI
        // silently no-ops on `spring unit revalidate`. Check registration
        // through the parser so a rename of the subcommand (e.g. to
        // `re-validate`) would trip this test first.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        unitCommand.Subcommands.ShouldContain(c => c.Name == "revalidate");
    }

    [Fact]
    public void UnitRevalidate_RequiresPositionalId()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit revalidate");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitRevalidate_AcceptsIdArgument()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit revalidate 8c5fab2a8e7e4b8da3e7d18c1a9f0b3c");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id").ShouldBe("8c5fab2a8e7e4b8da3e7d18c1a9f0b3c");
    }

    [Fact]
    public void UnitRevalidate_ParsesNoWaitFlag()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit revalidate eng-team --no-wait");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--no-wait").ShouldBeTrue();
    }

    [Fact]
    public void UnitCreate_ParsesNoWaitFlag()
    {
        // --no-wait on `create` is the opt-out for T-08's default-wait
        // behaviour; make sure the parser accepts it next to the existing
        // create options.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var parseResult = rootCommand.Parse("unit create eng-team --top-level --no-wait");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--no-wait").ShouldBeTrue();
    }

    [Fact]
    public async Task UnitCreate_HelpOutput_ContainsExitCodeTable()
    {
        // Help strings are the public contract operators read first.
        // Assert that the full table is there (numbers + code names) but
        // keep the match tolerant of whitespace layout tweaks so we're
        // not flipping on formatting churn.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var help = await CaptureHelpAsync(rootCommand, "unit create --help");

        help.ShouldContain("Exit codes:");
        foreach (var code in new[] { "20", "21", "22", "23", "24", "25", "26", "27" })
        {
            help.ShouldContain(code);
        }
        help.ShouldContain("ImagePullFailed");
        help.ShouldContain("CredentialInvalid");
        help.ShouldContain("ModelNotFound");
        help.ShouldContain("ProbeInternalError");
        help.ShouldContain("--no-wait");
    }

    [Fact]
    public async Task UnitRevalidate_HelpOutput_ContainsExitCodeTable()
    {
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        var help = await CaptureHelpAsync(rootCommand, "unit revalidate --help");

        help.ShouldContain("Exit codes:");
        help.ShouldContain("ImagePullFailed");
        help.ShouldContain("ProbeInternalError");
        help.ShouldContain("--no-wait");
    }

    /// <summary>
    /// Invokes <paramref name="rootCommand"/> with the given arg line and
    /// captures stdout. System.CommandLine's default help handler writes
    /// to <see cref="Console.Out"/> via the ParseResult invocation, so we
    /// redirect Console before calling InvokeAsync.
    /// </summary>
    private static async Task<string> CaptureHelpAsync(RootCommand rootCommand, string argLine)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await rootCommand.Parse(argLine)
                .InvokeAsync(configuration: null, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
