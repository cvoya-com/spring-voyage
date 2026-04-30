// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System;
using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// PR-C3 (#457): parser + window-resolver coverage for the `spring analytics`
/// command family. Wire-level behaviour is covered by
/// <see cref="SpringApiClientTests"/> so these tests focus on the flags users
/// type at the shell.
/// </summary>
public class AnalyticsCommandTests
{
    private static Option<string> CreateOutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };

    [Fact]
    public void AnalyticsCosts_ParsesWindowAndTargetOptions()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --window 7d --unit eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--window").ShouldBe("7d");
        parseResult.GetValue<string>("--unit").ShouldBe("eng-team");
    }

    [Fact]
    public void AnalyticsThroughput_ParsesUnitAndWindow()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics throughput --window 30d --unit eng-team");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--window").ShouldBe("30d");
        parseResult.GetValue<string>("--unit").ShouldBe("eng-team");
    }

    [Fact]
    public void AnalyticsWaits_ParsesAgentAndWindow()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics waits --window 24h --agent ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--window").ShouldBe("24h");
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
    }

    [Theory]
    [InlineData("24h")]
    [InlineData("7d")]
    [InlineData("30d")]
    [InlineData("90d")]
    [InlineData("90m")]
    [InlineData("600s")]
    public void ResolveWindow_AcceptsSupportedUnits_AndProducesPositiveRange(string window)
    {
        var (from, to) = AnalyticsCommand.ResolveWindow(window);

        from.ShouldNotBeNull();
        to.ShouldNotBeNull();
        to!.Value.ShouldBeGreaterThan(from!.Value);
    }

    [Fact]
    public void ResolveWindow_WithoutValue_ReturnsNull_ServerAppliesDefault()
    {
        var (from, to) = AnalyticsCommand.ResolveWindow(null);

        // Unresolved windows intentionally surface as (null, null) so the
        // server's 30-day default kicks in. Enforcing a CLI-side default
        // would split "no flag" into two variants across call sites.
        from.ShouldBeNull();
        to.ShouldBeNull();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("7x")]
    [InlineData("0d")]
    [InlineData("-3d")]
    [InlineData("abc")]
    public void ResolveWindow_RejectsInvalidLabels(string window)
    {
        Should.Throw<ArgumentException>(() => AnalyticsCommand.ResolveWindow(window));
    }

    // #554: --by-source / --breakdown flag tests.

    [Fact]
    public void AnalyticsCosts_ParsesBySourceFlag()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --by-source --window 7d");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--by-source").ShouldBeTrue();
        parseResult.GetValue<string>("--window").ShouldBe("7d");
    }

    [Fact]
    public void AnalyticsCosts_ParsesBreakdownAlias()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        // --breakdown is an alias for --by-source.
        var parseResult = root.Parse("analytics costs --breakdown");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--by-source").ShouldBeTrue();
    }

    [Fact]
    public void AnalyticsCosts_BySourceDefault_IsFalse()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --window 7d");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--by-source").ShouldBeFalse();
    }

    // #1361: --series flag tests.

    [Fact]
    public void AnalyticsCosts_ParsesSeriesFlagWithAgent()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --agent ada --series");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<bool>("--series").ShouldBeTrue();
    }

    [Fact]
    public void AnalyticsCosts_ParsesSeriesFlagWithUnit()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --unit eng-team --series");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--unit").ShouldBe("eng-team");
        parseResult.GetValue<bool>("--series").ShouldBeTrue();
    }

    [Fact]
    public void AnalyticsCosts_ParsesSeriesWithBucket()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --agent ada --series --bucket 1h");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--series").ShouldBeTrue();
        parseResult.GetValue<string>("--bucket").ShouldBe("1h");
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("1d")]
    [InlineData("7d")]
    public void AnalyticsCosts_BucketOption_AcceptsValidValues(string bucket)
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse($"analytics costs --agent ada --series --bucket {bucket}");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--bucket").ShouldBe(bucket);
    }

    [Fact]
    public void AnalyticsCosts_BucketOption_RejectsInvalidValue()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        // "3d" is not one of the allowed bucket values.
        var parseResult = root.Parse("analytics costs --agent ada --series --bucket 3d");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AnalyticsCosts_SeriesDefault_IsFalse()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --agent ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--series").ShouldBeFalse();
    }

    // #1362: --breakdown with --agent → per-agent model breakdown tests.

    [Fact]
    public void AnalyticsCosts_ParsesBreakdownWithAgent()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --agent ada --breakdown");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        // --breakdown is an alias for --by-source.
        parseResult.GetValue<bool>("--by-source").ShouldBeTrue();
    }

    [Fact]
    public void AnalyticsCosts_BreakdownAlias_WithAgent_ParsesCorrectly()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        // --by-source (long form) combined with --agent should also work.
        var parseResult = root.Parse("analytics costs --agent ada --by-source");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<bool>("--by-source").ShouldBeTrue();
    }

    [Fact]
    public void AnalyticsCosts_BreakdownWithoutAgent_UsesSourceEndpoint()
    {
        // Without --agent, --breakdown still parses fine — routing to the
        // per-source dashboard endpoint is done at runtime, not parse time.
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --breakdown");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<bool>("--by-source").ShouldBeTrue();
        parseResult.GetValue<string?>("--agent").ShouldBeNull();
    }

    [Fact]
    public void AnalyticsCosts_BreakdownWithAgent_AndWindow_ParsesAll()
    {
        var outputOption = CreateOutputOption();
        var analyticsCommand = AnalyticsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(analyticsCommand);

        var parseResult = root.Parse("analytics costs --agent ada --breakdown --window 7d");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
        parseResult.GetValue<bool>("--by-source").ShouldBeTrue();
        parseResult.GetValue<string>("--window").ShouldBe("7d");
    }
}