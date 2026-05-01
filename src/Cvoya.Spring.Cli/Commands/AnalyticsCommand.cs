// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring analytics</c> command tree (#457). Three verbs share a
/// <c>--window</c> flag that resolves to a <c>(from, to)</c> pair: <c>costs</c>
/// hits <c>/api/v1/costs</c>, <c>throughput</c> hits <c>/api/v1/analytics/throughput</c>,
/// and <c>waits</c> hits <c>/api/v1/analytics/waits</c>. The legacy
/// <c>spring cost summary</c> alias built in <see cref="CostCommand"/> routes
/// through the same <c>costs</c> code path so drift between the two is
/// impossible.
/// </summary>
public static class AnalyticsCommand
{
    private sealed record CostRow(
        string Scope,
        string Target,
        string TotalCost,
        string WorkCost,
        string InitiativeCost,
        string Records,
        string From,
        string To);

    private static readonly OutputFormatter.Column<CostRow>[] CostColumns =
    {
        new("scope", r => r.Scope),
        new("target", r => r.Target),
        new("totalCost", r => r.TotalCost),
        new("workCost", r => r.WorkCost),
        new("initiativeCost", r => r.InitiativeCost),
        new("records", r => r.Records),
        new("from", r => r.From),
        new("to", r => r.To),
    };

    // #554: per-source breakdown row emitted by `spring analytics costs --by-source`.
    // The dashboard costs endpoint returns a flat list of (source, totalCost) tuples;
    // the table here mirrors the portal's Analytics breakdown panel.
    private sealed record CostBySourceRow(
        string Source,
        string TotalCost,
        string From,
        string To);

    private static readonly OutputFormatter.Column<CostBySourceRow>[] CostBySourceColumns =
    {
        new("source", r => r.Source),
        new("totalCost", r => r.TotalCost),
        new("from", r => r.From),
        new("to", r => r.To),
    };

    // #1361: cost time-series row emitted by `spring analytics costs --series`.
    // Hits /api/v1/tenant/analytics/{agents|units}/{id}/cost-timeseries.
    private sealed record CostTimeseriesRow(
        string T,
        string CostUsd);

    private static readonly OutputFormatter.Column<CostTimeseriesRow>[] CostTimeseriesColumns =
    {
        new("t", r => r.T),
        new("costUsd", r => r.CostUsd),
    };

    // #1362: per-agent model breakdown row emitted by `spring analytics costs --agent X --breakdown`.
    // Hits /api/v1/tenant/cost/agents/{id}/breakdown.
    private sealed record CostBreakdownEntryRow(
        string Key,
        string Kind,
        string TotalCost,
        string RecordCount);

    private static readonly OutputFormatter.Column<CostBreakdownEntryRow>[] CostBreakdownEntryColumns =
    {
        new("key", r => r.Key),
        new("kind", r => r.Kind),
        new("totalCost", r => r.TotalCost),
        new("records", r => r.RecordCount),
    };

    private sealed record ThroughputRow(
        string Source,
        string MessagesReceived,
        string MessagesSent,
        string Turns,
        string ToolCalls);

    private static readonly OutputFormatter.Column<ThroughputRow>[] ThroughputColumns =
    {
        new("source", r => r.Source),
        new("received", r => r.MessagesReceived),
        new("sent", r => r.MessagesSent),
        new("turns", r => r.Turns),
        new("toolCalls", r => r.ToolCalls),
    };

    private sealed record WaitRow(
        string Source,
        string IdleSeconds,
        string BusySeconds,
        string WaitingForHumanSeconds,
        string StateTransitions);

    private static readonly OutputFormatter.Column<WaitRow>[] WaitColumns =
    {
        new("source", r => r.Source),
        new("idleSec", r => r.IdleSeconds),
        new("busySec", r => r.BusySeconds),
        new("waitingHumanSec", r => r.WaitingForHumanSeconds),
        new("transitions", r => r.StateTransitions),
    };

    /// <summary>Creates the <c>analytics</c> command and wires its three subcommands.</summary>
    public static Command Create(Option<string> outputOption)
    {
        var analyticsCommand = new Command(
            "analytics",
            "Analytics rollups (costs, throughput, wait times) that power the portal's Analytics surface.");

        analyticsCommand.Subcommands.Add(CreateCostsCommand(outputOption));
        analyticsCommand.Subcommands.Add(CreateThroughputCommand(outputOption));
        analyticsCommand.Subcommands.Add(CreateWaitsCommand(outputOption));

        return analyticsCommand;
    }

    /// <summary>
    /// Builds the <c>analytics costs</c> subcommand. Exposed publicly so
    /// <see cref="CostCommand"/> can reuse the same body for its
    /// <c>cost summary</c> alias — parameterising the name + description
    /// lets both surfaces share one implementation.
    /// </summary>
    public static Command CreateCostsCommand(Option<string> outputOption)
        => CreateCostsCommand(outputOption,
            name: "costs",
            description: "Cost rollup over a window. Defaults to the tenant total; pass --unit or --agent to scope.");

    /// <summary>
    /// Overload for callers that need a different verb name / description — the
    /// legacy <c>spring cost summary</c> alias is the current consumer.
    /// </summary>
    public static Command CreateCostsCommand(Option<string> outputOption, string name, string description)
    {
        var windowOption = CreateWindowOption();
        var unitOption = new Option<string?>("--unit")
        {
            Description = "Filter the rollup to a specific unit (mutually exclusive with --agent).",
        };
        var agentOption = new Option<string?>("--agent")
        {
            Description = "Filter the rollup to a specific agent (mutually exclusive with --unit).",
        };
        // #554: per-source breakdown flag. When set and --agent is NOT supplied,
        // the command hits the dashboard costs endpoint and returns a per-source
        // breakdown table. When --agent IS supplied with --breakdown, it routes to
        // the per-agent model-breakdown endpoint (#1362).
        var bySourceOption = new Option<bool>("--by-source")
        {
            Description =
                "Show cost broken down by source (agent / unit). Calls the dashboard " +
                "costs endpoint and prints a per-source table. " +
                "When combined with --agent, shows the per-model breakdown for that agent " +
                "instead (hits the agent breakdown endpoint). " +
                "--unit is ignored when --by-source is set.",
            DefaultValueFactory = _ => false,
        };
        bySourceOption.Aliases.Add("--breakdown");

        // #1361: --series flag. When set, the command hits the cost-timeseries
        // endpoint for the supplied --agent or --unit and prints a bucketed table.
        // Requires either --agent or --unit; --series on the tenant scope is not
        // supported (no tenant-level timeseries endpoint).
        var seriesOption = new Option<bool>("--series")
        {
            Description =
                "Show cost as a bucketed time-series (sparkline data). Requires --agent or --unit. " +
                "Use --bucket to select bucket size (1h, 1d, 7d; default 1d) and " +
                "--window to control the time span (default 30d).",
            DefaultValueFactory = _ => false,
        };

        var bucketOption = new Option<string?>("--bucket")
        {
            Description = "Bucket size for --series output: 1h, 1d, or 7d (default: 1d).",
        };
        bucketOption.AcceptOnlyFromAmong("1h", "1d", "7d");

        var command = new Command(name, description);
        command.Options.Add(windowOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(bySourceOption);
        command.Options.Add(seriesOption);
        command.Options.Add(bucketOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var window = parseResult.GetValue(windowOption);
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var bySource = parseResult.GetValue(bySourceOption);
            var series = parseResult.GetValue(seriesOption);
            var bucket = parseResult.GetValue(bucketOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!string.IsNullOrEmpty(unit) && !string.IsNullOrEmpty(agent))
            {
                await Console.Error.WriteLineAsync("Refusing to run with both --unit and --agent. Choose one.");
                Environment.Exit(1);
                return;
            }

            var (from, to) = ResolveWindow(window);
            var client = ClientFactory.Create();

            // #1361: --series mode. Requires --agent or --unit scope.
            if (series)
            {
                if (string.IsNullOrEmpty(agent) && string.IsNullOrEmpty(unit))
                {
                    await Console.Error.WriteLineAsync(
                        "--series requires --agent <name> or --unit <name>. " +
                        "There is no tenant-level timeseries endpoint.");
                    Environment.Exit(1);
                    return;
                }

                // The analytics timeseries endpoints accept the window as a
                // server-side string (e.g. "30d") rather than from/to offsets.
                // Pass the raw window label through; the server applies its own
                // default (30d) when omitted — matching the scalar cost path.
                if (!string.IsNullOrEmpty(agent))
                {
                    var timeseries = await client.GetAgentCostTimeseriesAsync(agent, window, bucket, ct);
                    RenderTimeseries(timeseries, output);
                }
                else
                {
                    var timeseries = await client.GetUnitCostTimeseriesAsync(unit!, window, bucket, ct);
                    RenderTimeseries(timeseries, output);
                }
                return;
            }

            // #1362: --breakdown (--by-source) with --agent → per-agent model breakdown.
            // Without --agent, falls through to the existing per-source dashboard path.
            if (bySource && !string.IsNullOrEmpty(agent))
            {
                var agentBreakdown = await client.GetAgentCostBreakdownAsync(agent, from, to, ct);
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(agentBreakdown));
                }
                else
                {
                    var rows = new List<CostBreakdownEntryRow>();
                    foreach (var entry in agentBreakdown.Entries ?? new List<CostBreakdownEntryResponse>())
                    {
                        rows.Add(new CostBreakdownEntryRow(
                            entry.Key ?? string.Empty,
                            entry.Kind ?? string.Empty,
                            (entry.TotalCost ?? 0).ToString("0.####", CultureInfo.InvariantCulture),
                            (entry.RecordCount ?? 0).ToString(CultureInfo.InvariantCulture)));
                    }
                    Console.WriteLine(OutputFormatter.FormatTable(rows, CostBreakdownEntryColumns));
                }
                return;
            }

            // #554: --by-source without --agent routes to the dashboard costs endpoint
            // and returns a per-source breakdown table. Unchanged from original behaviour.
            if (bySource)
            {
                var breakdown = await client.GetCostBreakdownAsync(from, to, ct);
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(breakdown));
                }
                else
                {
                    var rows = new List<CostBySourceRow>();
                    foreach (var entry in breakdown.CostsBySource ?? new List<CostBySource>())
                    {
                        rows.Add(new CostBySourceRow(
                            entry.Source ?? string.Empty,
                            (entry.TotalCost ?? 0).ToString("0.####", CultureInfo.InvariantCulture),
                            FormatTimestamp(breakdown.PeriodStart),
                            FormatTimestamp(breakdown.PeriodEnd)));
                    }
                    Console.WriteLine(OutputFormatter.FormatTable(rows, CostBySourceColumns));
                }
                return;
            }

            CostSummaryResponse result;
            string scope;
            string target;

            if (!string.IsNullOrEmpty(unit))
            {
                result = await client.GetUnitCostAsync(unit, from, to, ct);
                scope = "unit";
                target = unit;
            }
            else if (!string.IsNullOrEmpty(agent))
            {
                result = await client.GetAgentCostAsync(agent, from, to, ct);
                scope = "agent";
                target = agent;
            }
            else
            {
                result = await client.GetTenantCostAsync(from, to, ct);
                scope = "tenant";
                target = "default";
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var row = new CostRow(
                    scope,
                    target,
                    (result.TotalCost ?? 0).ToString("0.####", CultureInfo.InvariantCulture),
                    (result.WorkCost ?? 0).ToString("0.####", CultureInfo.InvariantCulture),
                    (result.InitiativeCost ?? 0).ToString("0.####", CultureInfo.InvariantCulture),
                    (result.RecordCount ?? 0).ToString(CultureInfo.InvariantCulture),
                    FormatTimestamp(result.From),
                    FormatTimestamp(result.To));
                Console.WriteLine(OutputFormatter.FormatTable(row, CostColumns));
            }
        });

        return command;
    }

    private static void RenderTimeseries(
        Cvoya.Spring.Cli.Generated.Models.AnalyticsCostTimeseriesResponse timeseries,
        string output)
    {
        if (output == "json")
        {
            Console.WriteLine(OutputFormatter.FormatJson(timeseries));
        }
        else
        {
            var rows = new List<CostTimeseriesRow>();
            foreach (var point in timeseries.Points ?? new List<Cvoya.Spring.Cli.Generated.Models.AnalyticsCostTimeseriesBucketResponse>())
            {
                rows.Add(new CostTimeseriesRow(
                    point.T is DateTimeOffset dto
                        ? dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : string.Empty,
                    (point.CostUsd ?? 0).ToString("0.######", CultureInfo.InvariantCulture)));
            }
            Console.WriteLine(OutputFormatter.FormatTable(rows, CostTimeseriesColumns));
        }
    }

    private static Command CreateThroughputCommand(Option<string> outputOption)
    {
        var windowOption = CreateWindowOption();
        var unitOption = new Option<string?>("--unit")
        {
            Description = "Filter throughput to the given unit (matches sources whose address contains 'unit://{name}').",
        };
        var agentOption = new Option<string?>("--agent")
        {
            Description = "Filter throughput to the given agent (matches sources whose address contains 'agent://{name}').",
        };
        var command = new Command(
            "throughput",
            "Message / turn / tool-call counts broken down by source over a window.");
        command.Options.Add(windowOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var window = parseResult.GetValue(windowOption);
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!string.IsNullOrEmpty(unit) && !string.IsNullOrEmpty(agent))
            {
                await Console.Error.WriteLineAsync("Refusing to run with both --unit and --agent. Choose one.");
                Environment.Exit(1);
                return;
            }

            var (from, to) = ResolveWindow(window);
            var sourceFilter = ResolveSourceFilter(unit, agent);
            var client = ClientFactory.Create();

            var result = await client.GetThroughputAsync(sourceFilter, from, to, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var rows = new List<ThroughputRow>();
                foreach (var entry in result.Entries ?? new List<ThroughputEntryResponse>())
                {
                    rows.Add(new ThroughputRow(
                        entry.Source ?? string.Empty,
                        (entry.MessagesReceived ?? 0L).ToString(CultureInfo.InvariantCulture),
                        (entry.MessagesSent ?? 0L).ToString(CultureInfo.InvariantCulture),
                        (entry.Turns ?? 0L).ToString(CultureInfo.InvariantCulture),
                        (entry.ToolCalls ?? 0L).ToString(CultureInfo.InvariantCulture)));
                }

                Console.WriteLine(OutputFormatter.FormatTable(rows, ThroughputColumns));
            }
        });

        return command;
    }

    private static Command CreateWaitsCommand(Option<string> outputOption)
    {
        var windowOption = CreateWindowOption();
        var unitOption = new Option<string?>("--unit")
        {
            Description = "Filter wait-time rollups to the given unit.",
        };
        var agentOption = new Option<string?>("--agent")
        {
            Description = "Filter wait-time rollups to the given agent.",
        };
        var command = new Command(
            "waits",
            "Per-source wait-time rollups: idle / busy / waiting-for-human durations derived from paired StateChanged lifecycle transitions, plus the raw StateChanged event count.");
        command.Options.Add(windowOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var window = parseResult.GetValue(windowOption);
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!string.IsNullOrEmpty(unit) && !string.IsNullOrEmpty(agent))
            {
                await Console.Error.WriteLineAsync("Refusing to run with both --unit and --agent. Choose one.");
                Environment.Exit(1);
                return;
            }

            var (from, to) = ResolveWindow(window);
            var sourceFilter = ResolveSourceFilter(unit, agent);
            var client = ClientFactory.Create();

            var result = await client.GetWaitTimesAsync(sourceFilter, from, to, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var rows = new List<WaitRow>();
                foreach (var entry in result.Entries ?? new List<WaitTimeEntryResponse>())
                {
                    rows.Add(new WaitRow(
                        entry.Source ?? string.Empty,
                        (entry.IdleSeconds ?? 0d).ToString("0.##", CultureInfo.InvariantCulture),
                        (entry.BusySeconds ?? 0d).ToString("0.##", CultureInfo.InvariantCulture),
                        (entry.WaitingForHumanSeconds ?? 0d).ToString("0.##", CultureInfo.InvariantCulture),
                        (entry.StateTransitions ?? 0L).ToString(CultureInfo.InvariantCulture)));
                }

                Console.WriteLine(OutputFormatter.FormatTable(rows, WaitColumns));
            }
        });

        return command;
    }

    private static Option<string?> CreateWindowOption() =>
        new("--window")
        {
            Description = "Rollup window, e.g. '24h', '7d', '30d', '90d'. Defaults to '30d' when unset.",
        };

    /// <summary>
    /// Maps a window label like <c>7d</c>, <c>24h</c>, <c>90m</c>, or <c>30s</c>
    /// to a <c>(from, to)</c> pair whose <c>to</c> is <c>UtcNow</c>. Returning
    /// <c>null</c> members forces the server to apply its default (30d) so
    /// the CLI and server agree on the fallback path. Invalid labels throw
    /// so a typo never silently becomes "last 30 days."
    /// </summary>
    public static (DateTimeOffset? From, DateTimeOffset? To) ResolveWindow(string? window)
    {
        if (string.IsNullOrWhiteSpace(window))
        {
            // Let the server apply its own default (30d). Both CLI surfaces
            // and the portal observe the same behaviour this way.
            return (null, null);
        }

        var trimmed = window.Trim();
        var unitChar = char.ToLowerInvariant(trimmed[^1]);
        var valuePart = trimmed[..^1];
        if (!int.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new ArgumentException(
                $"Invalid --window '{window}'. Expected a positive integer suffixed by s/m/h/d (e.g. '7d').");
        }

        var to = DateTimeOffset.UtcNow;
        DateTimeOffset from = unitChar switch
        {
            's' => to.AddSeconds(-value),
            'm' => to.AddMinutes(-value),
            'h' => to.AddHours(-value),
            'd' => to.AddDays(-value),
            _ => throw new ArgumentException(
                $"Invalid --window '{window}'. Unit must be s/m/h/d."),
        };
        return (from, to);
    }

    /// <summary>
    /// Builds the source-address filter the analytics throughput / waits
    /// endpoints accept. The server uses substring matching on the
    /// <c>scheme://path</c> source, so <c>unit://eng-team</c> and
    /// <c>agent://ada</c> select exactly those entities.
    /// </summary>
    private static string? ResolveSourceFilter(string? unit, string? agent)
    {
        if (!string.IsNullOrEmpty(unit))
        {
            return $"unit://{unit}";
        }

        if (!string.IsNullOrEmpty(agent))
        {
            return $"agent://{agent}";
        }

        return null;
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value is DateTimeOffset dto ? dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
}