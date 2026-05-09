// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "activity" command tree for querying activity events.
/// </summary>
public static class ActivityCommand
{
    private sealed record ActivityRow(
        string Timestamp,
        string Source,
        string EventType,
        string Severity,
        string Summary);

    private static readonly OutputFormatter.Column<ActivityRow>[] ActivityColumns =
    {
        new("timestamp", r => r.Timestamp),
        new("source", r => r.Source),
        new("type", r => r.EventType),
        new("severity", r => r.Severity),
        new("summary", r => r.Summary),
    };

    /// <summary>
    /// Creates the "activity" command with subcommands for querying events.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var activityCommand = new Command("activity", "Query activity events");

        activityCommand.Subcommands.Add(CreateListCommand(outputOption));

        return activityCommand;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var sourceOption = new Option<string?>("--source")
        {
            Description = "Filter by event source (e.g. unit:my-unit, agent:my-agent)",
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter by event type (e.g. MessageReceived, StateChanged)",
        };
        var severityOption = new Option<string?>("--severity")
        {
            Description = "Filter by minimum severity (Debug, Info, Warning, Error)",
        };
        severityOption.AcceptOnlyFromAmong("Debug", "Info", "Warning", "Error");
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum number of events to return (default 50)",
        };

        var command = new Command("list", "List activity events with optional filters");
        command.Options.Add(sourceOption);
        command.Options.Add(typeOption);
        command.Options.Add(severityOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var type = parseResult.GetValue(typeOption);
            var severity = parseResult.GetValue(severityOption);
            var limit = parseResult.GetValue(limitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var result = await client.QueryActivityAsync(
                source: source,
                eventType: type,
                severity: severity,
                pageSize: limit,
                ct: ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var items = result.Items ?? [];
                var rows = new List<ActivityRow>();
                foreach (var item in items)
                {
                    var ts = item.Timestamp is DateTimeOffset dto
                        ? dto.ToString("yyyy-MM-dd HH:mm:ss")
                        : string.Empty;

                    // Truncate summary to a reasonable terminal width.
                    var summary = item.Summary ?? string.Empty;
                    if (summary.Length > 80)
                    {
                        summary = string.Concat(summary.AsSpan(0, 77), "...");
                    }

                    rows.Add(new ActivityRow(
                        Timestamp: ts,
                        Source: item.Source ?? string.Empty,
                        EventType: item.EventType ?? string.Empty,
                        Severity: item.Severity ?? string.Empty,
                        Summary: summary));
                }

                Console.WriteLine(OutputFormatter.FormatTable(rows, ActivityColumns));
            }
        });

        return command;
    }
}
