// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.Generated.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the pure helpers + command-tree shape of
/// <see cref="DirectoryCommand"/>. The wire-layer (search endpoint
/// round-trip) is exercised from the Host.Api integration tests — here
/// we pin the parsing + subcommand wiring so regressions surface without
/// spinning up an HTTP harness (#528).
/// </summary>
[Collection(ConsoleRedirectionCollection.Name)]
public class DirectoryCommandTests
{
    [Fact]
    public void ParseAddress_Valid_ReturnsSchemeAndPath()
    {
        var parsed = DirectoryCommand.ParseAddress("agent://ada");
        parsed.ShouldNotBeNull();
        parsed!.Value.Scheme.ShouldBe("agent");
        parsed.Value.Path.ShouldBe("ada");
    }

    [Fact]
    public void ParseAddress_UnitScheme_ReturnsPath()
    {
        var parsed = DirectoryCommand.ParseAddress("unit://platform/foundation");
        parsed.ShouldNotBeNull();
        parsed!.Value.Scheme.ShouldBe("unit");
        parsed.Value.Path.ShouldBe("platform/foundation");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("://empty-scheme")]
    [InlineData("scheme://")]
    public void ParseAddress_Invalid_ReturnsNull(string address)
    {
        DirectoryCommand.ParseAddress(address).ShouldBeNull();
    }

    [Fact]
    public void Create_WiresAllThreeBrowseVerbs()
    {
        // `search` was added in #542; `list` + `show` arrive with #528.
        // Lock the set so a future refactor that drops a verb breaks this
        // test rather than silently regressing CLI/portal parity.
        var outputOption = new Option<string>("--output", "-o")
        {
            DefaultValueFactory = _ => "table",
        };

        var cmd = DirectoryCommand.Create(outputOption);

        cmd.Name.ShouldBe("directory");
        var verbs = cmd.Subcommands.Select(s => s.Name).ToArray();
        verbs.ShouldContain("search");
        verbs.ShouldContain("list");
        verbs.ShouldContain("show");
    }

    [Fact]
    public void ListCommand_DeclaresExpectedFilters()
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            DefaultValueFactory = _ => "table",
        };

        var list = DirectoryCommand.Create(outputOption)
            .Subcommands
            .First(s => s.Name == "list");

        var optionNames = list.Options.Select(o => o.Name).ToArray();
        optionNames.ShouldContain("--domain");
        optionNames.ShouldContain("--owner");
        optionNames.ShouldContain("--limit");
        optionNames.ShouldContain("--offset");
        optionNames.ShouldContain("--typed-only");
        optionNames.ShouldContain("--inside");
    }

    [Fact]
    public void ShowCommand_RequiresSlugArgument()
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            DefaultValueFactory = _ => "table",
        };

        var show = DirectoryCommand.Create(outputOption)
            .Subcommands
            .First(s => s.Name == "show");

        show.Arguments.Count.ShouldBe(1);
        show.Arguments[0].Name.ShouldBe("slug");
    }

    [Fact]
    public void RenderShow_MultiLevelProjection_RendersAncestorChainAsBreadcrumb()
    {
        // #553: a multi-level projected entry must render its ancestor
        // chain as a breadcrumb-style "unit://mid -> unit://root" trail
        // and list each `projection/{slug}` path on its own line under a
        // "Projected via" heading.
        var hit = new DirectorySearchHitResponse
        {
            Slug = "translation",
            Domain = new ExpertiseDomainDto
            {
                Name = "translation",
                Description = "Translation expertise",
                Level = "advanced",
            },
            Owner = new AddressDto { Scheme = "unit", Path = "origin" },
            OwnerDisplayName = "Origin",
            AggregatingUnit = new DirectorySearchHitResponse.DirectorySearchHitResponse_aggregatingUnit
            {
                AddressDto = new AddressDto { Scheme = "unit", Path = "root" },
            },
            TypedContract = false,
            Score = 20.0,
            MatchReason = "aggregated coverage",
            AncestorChain = new List<AddressDto>
            {
                new AddressDto { Scheme = "unit", Path = "mid" },
                new AddressDto { Scheme = "unit", Path = "root" },
            },
            ProjectionPaths = new List<string>
            {
                "projection/translation",
                "projection/translation",
            },
        };

        var output = CaptureStdout(() => DirectoryCommand.RenderShow(hit));

        // Breadcrumb joins ancestors with " -> "; keep the raw substring
        // match so a trailing whitespace / padding tweak doesn't fail the
        // test.
        output.ShouldContain("Ancestor chain");
        output.ShouldContain("unit://mid -> unit://root");
        // "Projected via" block lists one path per line.
        output.ShouldContain("Projected via:");
        // Two projection paths → two `projection/translation` lines.
        var projectionLineCount = output
            .Split('\n')
            .Count(line => line.Contains("projection/translation"));
        projectionLineCount.ShouldBe(2);
    }

    [Fact]
    public void RenderShow_DirectHit_RendersDirectSentinelAndNoProjectionBlock()
    {
        // #553: a direct hit should show "(direct)" for the ancestor
        // chain and must not emit the "Projected via" block so operators
        // see a single clean affordance rather than an empty list.
        var hit = new DirectorySearchHitResponse
        {
            Slug = "python",
            Domain = new ExpertiseDomainDto
            {
                Name = "python",
                Description = "Python expertise",
                Level = "advanced",
            },
            Owner = new AddressDto { Scheme = "unit", Path = "eng" },
            OwnerDisplayName = "Engineering",
            AggregatingUnit = null,
            TypedContract = false,
            Score = 100.0,
            MatchReason = "exact slug",
            AncestorChain = new List<AddressDto>(),
            ProjectionPaths = new List<string>(),
        };

        var output = CaptureStdout(() => DirectoryCommand.RenderShow(hit));

        output.ShouldContain("Ancestor chain");
        output.ShouldContain("(direct)");
        output.ShouldNotContain("Projected via:");
    }

    private static string CaptureStdout(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}