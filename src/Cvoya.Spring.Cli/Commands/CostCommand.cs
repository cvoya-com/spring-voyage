// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.Globalization;

using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring cost</c> command tree (#459 / PR-C3). Today this
/// surfaces the <c>set-budget</c> write verb plus a <c>summary</c> alias that
/// forwards to <c>spring analytics costs</c> so callers with muscle memory
/// keep working while the portal moves to the Analytics surface (#448).
/// </summary>
public static class CostCommand
{
    private sealed record BudgetRow(string Scope, string Target, string DailyBudget, string? DerivedFrom);

    private static readonly OutputFormatter.Column<BudgetRow>[] BudgetColumns =
    {
        new("scope", r => r.Scope),
        new("target", r => r.Target),
        new("dailyBudget", r => r.DailyBudget),
        new("derivedFrom", r => r.DerivedFrom),
    };

    /// <summary>
    /// Creates the <c>cost</c> command with its two initial verbs. Keeps the
    /// command tree open for future read verbs (<c>breakdown</c>, <c>budget</c>)
    /// without another refactor.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var costCommand = new Command(
            "cost",
            "Cost management: read rollups and write budgets. Prefer 'spring analytics costs' for new scripts.");

        costCommand.Subcommands.Add(CreateSummaryAliasCommand(outputOption));
        costCommand.Subcommands.Add(CreateSetBudgetCommand(outputOption));

        return costCommand;
    }

    private static Command CreateSummaryAliasCommand(Option<string> outputOption)
    {
        // Delegate to AnalyticsCommand.CreateCostsCommand so the alias body
        // cannot drift from its source of truth. The deprecation blurb in the
        // description matches §5.7 of portal-exploration ("Costs tab is what
        // was /budgets, promoted") so callers see the migration path.
        return AnalyticsCommand.CreateCostsCommand(
            outputOption,
            name: "summary",
            description: "[deprecated alias] Same as 'spring analytics costs'. Kept for backward compatibility; please migrate to the analytics command.");
    }

    private static Command CreateSetBudgetCommand(Option<string> outputOption)
    {
        var scopeOption = new Option<string>("--scope")
        {
            Description = "Budget scope: tenant, unit, or agent.",
            Required = true,
        };
        scopeOption.AcceptOnlyFromAmong("tenant", "unit", "agent");

        var targetOption = new Option<string?>("--target")
        {
            Description = "Target identifier (unit name, agent id, or tenant id). Omit for tenant scope to use the default tenant.",
        };

        var amountOption = new Option<decimal>("--amount")
        {
            Description = "Budget amount in USD for the given --period.",
            Required = true,
        };

        var periodOption = new Option<string>("--period")
        {
            Description = "Period for the amount. The server stores daily budgets; weekly/monthly amounts are normalised (amount/7 or amount/30).",
            DefaultValueFactory = _ => "daily",
        };
        periodOption.AcceptOnlyFromAmong("daily", "weekly", "monthly");

        var command = new Command(
            "set-budget",
            "Set the cost budget for a tenant, unit, or agent. Mirrors the portal's 'Edit budget' action.");
        command.Options.Add(scopeOption);
        command.Options.Add(targetOption);
        command.Options.Add(amountOption);
        command.Options.Add(periodOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var scope = parseResult.GetValue(scopeOption)!;
            var target = parseResult.GetValue(targetOption);
            var amount = parseResult.GetValue(amountOption);
            var period = parseResult.GetValue(periodOption) ?? "daily";
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (amount <= 0m)
            {
                await Console.Error.WriteLineAsync("--amount must be greater than zero.");
                Environment.Exit(1);
                return;
            }

            if ((scope == "unit" || scope == "agent") && string.IsNullOrEmpty(target))
            {
                await Console.Error.WriteLineAsync($"--target is required for --scope {scope}.");
                Environment.Exit(1);
                return;
            }

            var dailyAmount = NormaliseToDailyBudget(amount, period);
            var client = ClientFactory.Create();

            Generated.Models.BudgetResponse result = scope switch
            {
                "tenant" => await client.SetTenantBudgetAsync(dailyAmount, tenantId: target, ct),
                "unit" => await client.SetUnitBudgetAsync(target!, dailyAmount, ct),
                "agent" => await client.SetAgentBudgetAsync(target!, dailyAmount, ct),
                _ => throw new InvalidOperationException($"Unsupported scope '{scope}'."),
            };

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var derivedFrom = period == "daily"
                    ? null
                    : $"{amount.ToString("0.####", CultureInfo.InvariantCulture)} {period}";
                var row = new BudgetRow(
                    scope,
                    target ?? (scope == "tenant" ? "default" : string.Empty),
                    (result.DailyBudget ?? 0d).ToString("0.####", CultureInfo.InvariantCulture),
                    derivedFrom);
                Console.WriteLine(OutputFormatter.FormatTable(row, BudgetColumns));
            }
        });

        return command;
    }

    /// <summary>
    /// Converts a <paramref name="amount"/> expressed in the given
    /// <paramref name="period"/> into the daily figure the server stores.
    /// Weekly uses a 7-day divisor; monthly uses a flat 30-day divisor to
    /// match the portal's budget-utilisation calculations without depending
    /// on calendar math.
    /// </summary>
    public static decimal NormaliseToDailyBudget(decimal amount, string period) =>
        period switch
        {
            "daily" => amount,
            "weekly" => Math.Round(amount / 7m, 4, MidpointRounding.AwayFromZero),
            "monthly" => Math.Round(amount / 30m, 4, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentException($"Unsupported period '{period}'. Expected daily, weekly, or monthly."),
        };
}