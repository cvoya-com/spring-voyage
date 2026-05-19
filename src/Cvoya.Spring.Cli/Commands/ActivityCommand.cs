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

        // #2492: `spring activity tail` — live SSE stream of activity events
        // with optional source / thread / message / kind / from / severity filters.
        activityCommand.Subcommands.Add(ActivityTailCommand.CreateActivityTail());

        // #2492: tenant capture-level + retention settings verbs.
        activityCommand.Subcommands.Add(CreateSettingsCommand(outputOption));

        return activityCommand;
    }

    private static Command CreateSettingsCommand(Option<string> outputOption)
    {
        var settingsCommand = new Command(
            "settings",
            "Get or set the tenant's activity-capture settings (level + retention). Issue #2492.");

        var showCommand = new Command("show", "Show the current capture level and retention horizon.");
        showCommand.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var client = ClientFactory.Create();
            var output = parseResult.GetValue(outputOption) ?? "table";
            var snapshot = await client.GetActivitySettingsAsync(ct);
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(snapshot));
            }
            else
            {
                Console.WriteLine($"level:           {snapshot.Level}");
                Console.WriteLine($"retention_days:  {snapshot.RetentionDays}");
            }
        });
        settingsCommand.Subcommands.Add(showCommand);

        var levelOption = new Option<string?>("--level")
        {
            Description = "Capture level: off | summary | full.",
        };
        levelOption.AcceptOnlyFromAmong("off", "summary", "full");
        var retentionOption = new Option<int?>("--retention-days")
        {
            Description = "Activity retention horizon in days (must be > 0).",
        };
        var setCommand = new Command(
            "set",
            "Update the tenant's activity-capture settings. At least one of --level or --retention-days is required.");
        setCommand.Options.Add(levelOption);
        setCommand.Options.Add(retentionOption);
        setCommand.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var level = parseResult.GetValue(levelOption);
            var retention = parseResult.GetValue(retentionOption);
            if (string.IsNullOrEmpty(level) && retention is null)
            {
                Console.Error.WriteLine("Pass at least one of --level / --retention-days.");
                Environment.Exit(2);
                return;
            }

            var client = ClientFactory.Create();
            var output = parseResult.GetValue(outputOption) ?? "table";
            var snapshot = await client.UpdateActivitySettingsAsync(level, retention, ct);
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(snapshot));
            }
            else
            {
                Console.WriteLine($"level:           {snapshot.Level}");
                Console.WriteLine($"retention_days:  {snapshot.RetentionDays}");
            }
        });
        settingsCommand.Subcommands.Add(setCommand);

        return settingsCommand;
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

            // When --source is `unit:<idOrName>` or `agent:<idOrName>`, resolve
            // the path segment through CliResolver so the wire filter carries
            // the canonical no-dash hex form the API expects.
            if (!string.IsNullOrWhiteSpace(source))
            {
                var colonIdx = source.IndexOf(':');
                if (colonIdx > 0)
                {
                    var scheme = source[..colonIdx];
                    var path = source[(colonIdx + 1)..];
                    if (!string.IsNullOrWhiteSpace(path)
                        && (scheme == "unit" || scheme == "agent"))
                    {
                        var resolver = new CliResolver(client);
                        try
                        {
                            var resolvedPath = scheme == "unit"
                                ? await resolver.ResolveUnitIdAsync(path, parentContext: null, ct)
                                : await resolver.ResolveAgentIdAsync(path, unitContext: null, ct);
                            source = $"{scheme}:{resolvedPath}";
                        }
                        catch (CliResolutionException ex)
                        {
                            CliResolutionPrinter.Write(Console.Error, ex);
                            Environment.Exit(1);
                            return;
                        }
                    }
                }
            }

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
