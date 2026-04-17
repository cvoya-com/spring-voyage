// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// PR-C3 (#459): `spring cost` command parser tests. The set-budget verb is
/// the new surface; `summary` is a deprecated alias that routes through
/// <see cref="AnalyticsCommand.CreateCostsCommand(Option{string})"/>.
/// </summary>
public class CostCommandTests
{
    private static Option<string> CreateOutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };

    [Fact]
    public void CostSetBudget_ParsesTenantScope()
    {
        var outputOption = CreateOutputOption();
        var costCommand = CostCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(costCommand);

        var parseResult = root.Parse("cost set-budget --scope tenant --amount 50 --period monthly");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--scope").ShouldBe("tenant");
        parseResult.GetValue<decimal>("--amount").ShouldBe(50m);
        parseResult.GetValue<string>("--period").ShouldBe("monthly");
    }

    [Fact]
    public void CostSetBudget_ParsesUnitScopeWithTarget()
    {
        var outputOption = CreateOutputOption();
        var costCommand = CostCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(costCommand);

        var parseResult = root.Parse(
            "cost set-budget --scope unit --target eng-team --amount 20 --period weekly");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--scope").ShouldBe("unit");
        parseResult.GetValue<string>("--target").ShouldBe("eng-team");
        parseResult.GetValue<decimal>("--amount").ShouldBe(20m);
        parseResult.GetValue<string>("--period").ShouldBe("weekly");
    }

    [Fact]
    public void CostSetBudget_ParsesAgentScopeWithDailyDefault()
    {
        var outputOption = CreateOutputOption();
        var costCommand = CostCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(costCommand);

        var parseResult = root.Parse(
            "cost set-budget --scope agent --target ada --amount 5");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--period").ShouldBe("daily");
        parseResult.GetValue<string>("--target").ShouldBe("ada");
    }

    [Fact]
    public void CostSetBudget_RejectsInvalidScope()
    {
        var outputOption = CreateOutputOption();
        var costCommand = CostCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(costCommand);

        var parseResult = root.Parse(
            "cost set-budget --scope global --amount 10");

        // 'global' is not in the accepted scope set; parser should surface an
        // error rather than silently coercing it.
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CostSetBudget_RejectsInvalidPeriod()
    {
        var outputOption = CreateOutputOption();
        var costCommand = CostCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(costCommand);

        var parseResult = root.Parse(
            "cost set-budget --scope tenant --amount 10 --period yearly");

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CostSummary_IsDeprecatedAliasOfAnalyticsCosts_AndParsesWindow()
    {
        // The legacy `spring cost summary` verb must keep parsing because
        // scripts and docs/guide/observing.md reference it.
        var outputOption = CreateOutputOption();
        var costCommand = CostCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(costCommand);

        var parseResult = root.Parse("cost summary --window 7d --agent ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--window").ShouldBe("7d");
        parseResult.GetValue<string>("--agent").ShouldBe("ada");
    }

    [Theory]
    [InlineData("daily", 21, 21)]
    [InlineData("weekly", 14, 2)]          // 14 / 7 = 2
    [InlineData("monthly", 60, 2)]         // 60 / 30 = 2
    public void NormaliseToDailyBudget_ConvertsPeriodToDaily(string period, decimal amount, decimal expectedDaily)
    {
        // The server only persists DailyBudget, so the CLI must normalise
        // weekly / monthly amounts locally; any drift between this helper
        // and the portal's budget-utilisation calculation would produce
        // surprising "$30 set but 93 % used" states.
        var actual = CostCommand.NormaliseToDailyBudget(amount, period);
        actual.ShouldBe(expectedDaily);
    }
}