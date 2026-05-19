// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Implements the <c>tail</c> subcommands: <c>spring agent tail</c>,
/// <c>spring unit tail</c>, <c>spring human tail</c>, and
/// <c>spring activity tail</c>. All four drive the same underlying SSE
/// subscription on <c>/api/v1/tenant/activity/stream</c>, differing only
/// in how the source filter is constructed. Issue #2492.
/// </summary>
public static class ActivityTailCommand
{
    private sealed class SharedOptions
    {
        public Option<string?>? Source;
        public Option<string?>? Unit;
        public Option<string?> Thread = null!;
        public Option<string?> Message = null!;
        public Option<string[]> Kind = null!;
        public Option<DateTimeOffset?> From = null!;
        public Option<string?> Severity = null!;
        public Option<bool> Json = null!;
    }

    /// <summary>Builds the <c>spring activity tail</c> subcommand.</summary>
    public static Command CreateActivityTail()
    {
        var command = new Command("tail", "Stream activity events in real time via SSE.");
        var shared = AddSharedOptions(command, includeSourceOption: true, includeUnitOption: true);
        command.SetAction((ParseResult parseResult, CancellationToken ct)
            => RunAsync(parseResult, shared, scheme: null, idArgument: null, unitMode: false, ct));
        return command;
    }

    /// <summary>Builds the <c>spring agent tail &lt;id&gt;</c> subcommand.</summary>
    public static Command CreateAgentTail()
    {
        var idArg = new Argument<string>("id")
        {
            Description = "Agent id or display name.",
        };
        var command = new Command("tail", "Stream activity events for one agent in real time.");
        command.Arguments.Add(idArg);
        var shared = AddSharedOptions(command, includeSourceOption: false, includeUnitOption: false);
        command.SetAction((ParseResult parseResult, CancellationToken ct)
            => RunAsync(parseResult, shared, scheme: "agent", idArg, unitMode: false, ct));
        return command;
    }

    /// <summary>Builds the <c>spring unit tail &lt;id&gt;</c> subcommand.</summary>
    public static Command CreateUnitTail()
    {
        var idArg = new Argument<string>("id")
        {
            Description = "Unit id or display name.",
        };
        var command = new Command("tail", "Stream activity events for one unit (and its descendants) in real time.");
        command.Arguments.Add(idArg);
        var shared = AddSharedOptions(command, includeSourceOption: false, includeUnitOption: false);
        command.SetAction((ParseResult parseResult, CancellationToken ct)
            => RunAsync(parseResult, shared, scheme: "unit", idArg, unitMode: true, ct));
        return command;
    }

    /// <summary>
    /// Builds the <c>spring human tail &lt;id&gt;</c> subcommand. Humans
    /// are activity subjects (#2492) — capturing messages sent / received
    /// and notifications dispatched — so the tail surface is symmetric
    /// with agents / units.
    /// </summary>
    public static Command CreateHumanTail()
    {
        var idArg = new Argument<string>("id")
        {
            Description = "Human id.",
        };
        var command = new Command("tail", "Stream conversation activity events for one human in real time.");
        command.Arguments.Add(idArg);
        var shared = AddSharedOptions(command, includeSourceOption: false, includeUnitOption: false);
        command.SetAction((ParseResult parseResult, CancellationToken ct)
            => RunAsync(parseResult, shared, scheme: "human", idArg, unitMode: false, ct));
        return command;
    }

    private static SharedOptions AddSharedOptions(Command command, bool includeSourceOption, bool includeUnitOption)
    {
        var shared = new SharedOptions
        {
            Thread = new Option<string?>("--thread")
            {
                Description = "Filter by thread id (CorrelationId on the activity event).",
            },
            Message = new Option<string?>("--message")
            {
                Description = "Filter by inbound message id (sv.message.id resource attribute).",
            },
            Kind = new Option<string[]>("--kind")
            {
                Description = "Filter by event kind. Repeat for multi-value (e.g. --kind RuntimeLog --kind LlmTurn).",
                AllowMultipleArgumentsPerToken = true,
            },
            From = new Option<DateTimeOffset?>("--from")
            {
                Description = "Skip events older than this ISO-8601 timestamp.",
            },
            Severity = new Option<string?>("--severity")
            {
                Description = "Filter by minimum severity (Debug, Info, Warning, Error).",
            },
            Json = new Option<bool>("--json")
            {
                Description = "Emit one JSON object per event (NDJSON) instead of the colored human format.",
            },
        };
        shared.Severity.AcceptOnlyFromAmong("Debug", "Info", "Warning", "Error");
        if (includeSourceOption)
        {
            shared.Source = new Option<string?>("--source")
            {
                Description = "Optional source filter (agent:<id> / unit:<id> / human:<id>).",
            };
            command.Options.Add(shared.Source);
        }
        if (includeUnitOption)
        {
            shared.Unit = new Option<string?>("--unit")
            {
                Description = "Subscribe to a unit's merged member stream (id or display name).",
            };
            command.Options.Add(shared.Unit);
        }
        command.Options.Add(shared.Thread);
        command.Options.Add(shared.Message);
        command.Options.Add(shared.Kind);
        command.Options.Add(shared.From);
        command.Options.Add(shared.Severity);
        command.Options.Add(shared.Json);
        return shared;
    }

    private static async Task RunAsync(
        ParseResult parseResult,
        SharedOptions shared,
        string? scheme,
        Argument<string>? idArgument,
        bool unitMode,
        CancellationToken ct)
    {
        string? source = null;
        string? unitId = null;

        if (scheme is not null && idArgument is not null)
        {
            var raw = parseResult.GetValue(idArgument);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.Error.WriteLine("Missing required <id>.");
                Environment.Exit(2);
                return;
            }

            var resolver = new CliResolver(ClientFactory.Create());
            try
            {
                var resolved = scheme switch
                {
                    "agent" => await resolver.ResolveAgentIdAsync(raw, unitContext: null, ct),
                    "unit" => await resolver.ResolveUnitIdAsync(raw, parentContext: null, ct),
                    "human" => raw, // Human id resolution stays lenient — pass the raw value through.
                    _ => raw,
                };
                if (unitMode)
                {
                    unitId = resolved;
                }
                else
                {
                    source = $"{scheme}:{resolved}";
                }
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }
        }
        else
        {
            source = shared.Source is null ? null : parseResult.GetValue(shared.Source);
            unitId = shared.Unit is null ? null : parseResult.GetValue(shared.Unit);
        }

        var thread = parseResult.GetValue(shared.Thread);
        var message = parseResult.GetValue(shared.Message);
        var kinds = parseResult.GetValue(shared.Kind) ?? Array.Empty<string>();
        var from = parseResult.GetValue(shared.From);
        var severity = parseResult.GetValue(shared.Severity);
        var jsonMode = parseResult.GetValue(shared.Json);

        var client = ClientFactory.Create();

        if (!jsonMode)
        {
            Console.Error.WriteLine("Tailing activity stream (Ctrl-C to stop)…");
        }

        await foreach (var payload in client.StreamActivityAsync(
            source: source,
            threadId: thread,
            messageId: message,
            kinds: kinds.Length > 0 ? kinds : null,
            from: from,
            severity: severity,
            unitId: unitId,
            ct: ct))
        {
            if (jsonMode)
            {
                Console.WriteLine(payload);
                continue;
            }

            RenderHumanLine(payload);
        }
    }

    private static void RenderHumanLine(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ts = root.TryGetProperty("Timestamp", out var t1) ? t1.GetString()
                : root.TryGetProperty("timestamp", out var t2) ? t2.GetString() : null;
            var eventType = root.TryGetProperty("EventType", out var e1) ? e1.GetString()
                : root.TryGetProperty("eventType", out var e2) ? e2.GetString() : null;
            var severity = root.TryGetProperty("Severity", out var s1) ? s1.GetString()
                : root.TryGetProperty("severity", out var s2) ? s2.GetString() : null;
            var summary = root.TryGetProperty("Summary", out var su1) ? su1.GetString()
                : root.TryGetProperty("summary", out var su2) ? su2.GetString() : null;
            string? sourceScheme = null;
            string? sourcePath = null;
            if (root.TryGetProperty("Source", out var src) && src.ValueKind == JsonValueKind.Object)
            {
                if (src.TryGetProperty("Scheme", out var sch)) sourceScheme = sch.GetString();
                if (src.TryGetProperty("Id", out var idEl)) sourcePath = idEl.GetString();
            }

            var color = severity switch
            {
                "Error" => ConsoleColor.Red,
                "Warning" => ConsoleColor.Yellow,
                "Debug" => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            var prior = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                var sourceLabel = sourceScheme is null || sourcePath is null
                    ? "?"
                    : $"{sourceScheme}:{sourcePath}";
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] {1,-8} {2,-22} {3,-32}  {4}",
                    ts ?? "-", severity ?? "-", eventType ?? "-", sourceLabel, summary ?? string.Empty));
            }
            finally
            {
                Console.ForegroundColor = prior;
            }
        }
        catch (JsonException)
        {
            // Fall back to raw if parsing fails.
            Console.WriteLine(payload);
        }
    }
}
