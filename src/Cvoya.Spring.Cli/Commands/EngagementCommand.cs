// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring engagement</c> verb family (E2.2 / #1414).
/// Engagements are the UX framing for threads — every subcommand here
/// maps 1:1 onto an existing <c>/api/v1/tenant/threads</c> endpoint so the
/// engagement portal and the CLI share the same API surface.
///
/// Subcommands:
/// <list type="bullet">
///   <item><c>list</c> — list threads (engagement view); filter by unit or agent</item>
///   <item><c>watch &lt;id&gt;</c> — stream the engagement's activity via SSE</item>
///   <item><c>send &lt;id&gt; &lt;message&gt;</c> — send a message into an engagement</item>
///   <item><c>answer &lt;id&gt; &lt;answer&gt;</c> — answer a clarifying question from a unit</item>
///   <item><c>errors &lt;id&gt;</c> — list first-class errors on an engagement's timeline</item>
/// </list>
///
/// API notes:
/// <list type="bullet">
///   <item>
///     <c>engagement list</c> (no flags) returns all threads visible to the
///     authenticated caller. Pass <c>--participant &lt;address&gt;</c> to filter
///     by participant address.
///   </item>
///   <item>
///     <c>engagement watch</c> connects to
///     <c>GET /api/v1/tenant/activity/stream?thread=&lt;id&gt;</c> so the server
///     filters events to the specified thread before they reach the wire (#1421).
///     A client-side fallback is retained for older servers.
///   </item>
///   <item>
///     <c>engagement send</c> sets <c>kind=information</c> (the default) and
///     <c>engagement answer</c> sets <c>kind=answer</c> (#1421). Both route to
///     the same <c>POST /api/v1/tenant/threads/{id}/messages</c> endpoint; the
///     <c>kind</c> discriminator is the semantic signal — not a routing gate.
///   </item>
/// </list>
/// </summary>
public static class EngagementCommand
{
    private static readonly OutputFormatter.Column<ThreadSummaryResponse>[] ListColumns =
    {
        new("id", c => c.Id),
        new("status", c => c.Status),
        new("participants", c => FormatParticipants(c.Participants)),
        new("events", c => c.EventCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        new("lastActivity", c => FormatTimestamp(c.LastActivity)),
        new("summary", c => Truncate(c.Summary, 60)),
    };

    private static readonly OutputFormatter.Column<ThreadEventResponse>[] ErrorColumns =
    {
        new("timestamp", e => FormatTimestamp(e.Timestamp)),
        new("source", e => e.Source?.DisplayName ?? e.Source?.Address ?? string.Empty),
        new("type", e => e.EventType ?? string.Empty),
        new("summary", e => Truncate(e.Summary, 80)),
    };

    /// <summary>
    /// Creates the <c>engagement</c> command tree.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("engagement", "Observe and participate in unit/agent engagements");
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateWatchCommand(outputOption));
        cmd.Subcommands.Add(CreateSendCommand(outputOption));
        cmd.Subcommands.Add(CreateAnswerCommand(outputOption));
        cmd.Subcommands.Add(CreateErrorsCommand(outputOption));
        return cmd;
    }

    // -----------------------------------------------------------------------
    // engagement list
    // -----------------------------------------------------------------------

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var unitOption = new Option<string?>("--unit")
        {
            Description = "List engagements involving this unit (id or slug)",
        };
        var agentOption = new Option<string?>("--agent")
        {
            Description = "List engagements involving this agent (id or slug)",
        };
        var participantOption = new Option<string?>("--participant")
        {
            Description = "Filter by participant address (e.g. human://alice, agent://ada)",
        };
        var statusOption = new Option<string?>("--status")
        {
            Description = "Filter by status (active | completed)",
        };
        statusOption.AcceptOnlyFromAmong("active", "completed");
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum rows to return (default 50)",
        };

        var command = new Command("list", "List engagements. Defaults to all threads visible to the authenticated caller; use --unit or --agent to scope.");
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(participantOption);
        command.Options.Add(statusOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var result = await client.ListThreadsAsync(
                    unit: parseResult.GetValue(unitOption),
                    agent: parseResult.GetValue(agentOption),
                    status: parseResult.GetValue(statusOption),
                    participant: parseResult.GetValue(participantOption),
                    limit: parseResult.GetValue(limitOption),
                    ct: ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(result, ListColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to list engagements: {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // engagement watch <id>
    // -----------------------------------------------------------------------

    private static Command CreateWatchCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The engagement (thread) id to observe" };
        var sourceOption = new Option<string?>("--source")
        {
            Description = "Additional source filter (e.g. agent://ada) — narrows the SSE stream",
        };

        var command = new Command(
            "watch",
            "Stream live activity from an engagement. Observe-mode — no participant status required. " +
            "Uses GET /api/v1/tenant/activity/stream?thread=<id> for server-side filtering (#1421); " +
            "a client-side fallback guards against older servers. Press Ctrl+C to stop.");
        command.Arguments.Add(idArg);
        command.Options.Add(sourceOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var source = parseResult.GetValue(sourceOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            Console.Error.WriteLine($"Watching engagement {id} — press Ctrl+C to stop...");

            try
            {
                await client.StreamEngagementAsync(
                    threadId: id,
                    source: source,
                    onEvent: (line) =>
                    {
                        // Each SSE line is `data: <json>`. Strip the prefix and render.
                        var json = line.StartsWith("data: ", StringComparison.Ordinal)
                            ? line["data: ".Length..]
                            : line;

                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return;
                        }

                        if (output == "json")
                        {
                            Console.WriteLine(json);
                            return;
                        }

                        // Best-effort table-row extraction from the raw SSE JSON.
                        // The server emits ActivityEvent records; parse the key
                        // fields without pulling in the Kiota-generated type (the
                        // activity SSE uses a different wire format from ThreadEvent).
                        try
                        {
                            var doc = System.Text.Json.JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            var ts = root.TryGetProperty("timestamp", out var tsProp)
                                ? tsProp.GetDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")
                                : string.Empty;
                            var evtType = root.TryGetProperty("eventType", out var etProp)
                                ? etProp.GetString() ?? string.Empty
                                : string.Empty;
                            var src = root.TryGetProperty("source", out var srcProp)
                                ? (srcProp.ValueKind == System.Text.Json.JsonValueKind.Object
                                    ? $"{srcProp.GetProperty("scheme").GetString()}://{srcProp.GetProperty("path").GetString()}"
                                    : srcProp.GetString() ?? string.Empty)
                                : string.Empty;
                            var summary = root.TryGetProperty("summary", out var sumProp)
                                ? sumProp.GetString() ?? string.Empty
                                : string.Empty;
                            var severity = root.TryGetProperty("severity", out var sevProp)
                                ? sevProp.GetString() ?? string.Empty
                                : string.Empty;

                            var isSevError = string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(evtType, "ErrorOccurred", StringComparison.Ordinal);
                            var prefix = isSevError ? "!!" : "  ";
                            var writer = isSevError ? Console.Error : Console.Out;
                            writer.WriteLine($"[{ts}] {prefix} [{src}] {evtType} — {summary}");
                        }
                        catch
                        {
                            // Fallback: emit the raw JSON line.
                            Console.WriteLine(json);
                        }
                    },
                    ct: ct);
            }
            catch (OperationCanceledException)
            {
                // User pressed Ctrl+C — clean exit.
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Engagement stream interrupted for '{id}': {ex.Message}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // engagement send <id> <message>
    // -----------------------------------------------------------------------

    private static Command CreateSendCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The engagement (thread) id to send into" };
        var messageArg = new Argument<string>("message") { Description = "Message text to send" };
        var addressArg = new Argument<string>("address")
        {
            Description = "Destination address (e.g. agent://ada, unit://engineering-team)",
        };

        var command = new Command(
            "send",
            "Send a message into an engagement. Requires participant status. " +
            "Supply the destination address so the message is routed to the right agent or unit.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(addressArg);
        command.Arguments.Add(messageArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var address = parseResult.GetValue(addressArg)!;
            var message = parseResult.GetValue(messageArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var (scheme, path) = AddressParser.Parse(address);
            var client = ClientFactory.Create();

            try
            {
                // engagement send defaults to kind=information (#1421).
                var result = await client.SendThreadMessageAsync(
                    id, scheme, path, message, kind: "information", ct: ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : $"Message sent to {address} in engagement {result.ThreadId}. (id: {result.MessageId?.ToString() ?? "n/a"})");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to send message into engagement '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // engagement answer <id> <answer>
    // -----------------------------------------------------------------------

    private static Command CreateAnswerCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The engagement (thread) id containing the question" };
        var answerArg = new Argument<string>("answer") { Description = "Your answer to the unit's clarifying question" };
        var addressArg = new Argument<string>("address")
        {
            Description = "Address of the agent or unit that asked the question (e.g. agent://ada)",
        };

        // `engagement answer` sends kind=answer so the portal can distinguish
        // replies to clarifying questions from unprompted sends (#1421).
        // Both `answer` and `send` route to the same endpoint; the `kind`
        // discriminator is the semantic signal — not a routing gate.
        var command = new Command(
            "answer",
            "Answer a clarifying question from a unit or agent. " +
            "Sends kind=answer to the POST /api/v1/tenant/threads/{id}/messages endpoint " +
            "so the engagement portal can identify replies semantically.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(addressArg);
        command.Arguments.Add(answerArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var address = parseResult.GetValue(addressArg)!;
            var answer = parseResult.GetValue(answerArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var (scheme, path) = AddressParser.Parse(address);
            var client = ClientFactory.Create();

            try
            {
                // engagement answer sets kind=answer — the semantic distinction
                // from engagement send is now explicit on the wire (#1421).
                var result = await client.SendThreadMessageAsync(
                    id, scheme, path, answer, kind: "answer", ct: ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : $"Answer sent to {address} in engagement {result.ThreadId}. (id: {result.MessageId?.ToString() ?? "n/a"})");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to send answer into engagement '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // engagement errors <id>
    // -----------------------------------------------------------------------

    private static Command CreateErrorsCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The engagement (thread) id to inspect" };

        var command = new Command(
            "errors",
            "List first-class errors from an engagement's timeline. " +
            "Shows Timeline entries where kind=ErrorOccurred or severity=Error. Read-only.");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var detail = await client.GetThreadAsync(id, ct);

                var events = detail.Events ?? new List<ThreadEventResponse>();
                var errors = events
                    .Where(e =>
                        string.Equals(e.EventType, "ErrorOccurred", StringComparison.Ordinal)
                        || string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(errors));
                    return;
                }

                if (errors.Count == 0)
                {
                    Console.WriteLine("No errors found in this engagement.");
                    return;
                }

                Console.WriteLine($"Engagement: {id}");
                Console.WriteLine($"Errors:     {errors.Count}");
                Console.WriteLine();
                Console.WriteLine(OutputFormatter.FormatTable(errors, ErrorColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to load engagement '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // Private helpers (mirrors ThreadCommand helpers)
    // -----------------------------------------------------------------------

    private static string FormatParticipants(IEnumerable<ParticipantRef>? participants)
    {
        if (participants is null)
        {
            return string.Empty;
        }

        var list = participants.Select(p => p.DisplayName ?? p.Address ?? string.Empty).ToList();
        return list.Count switch
        {
            0 => string.Empty,
            <= 3 => string.Join(", ", list),
            _ => $"{string.Join(", ", list.Take(3))} (+{list.Count - 3})",
        };
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is DateTimeOffset dto ? dto.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength
            ? text
            : string.Concat(text.AsSpan(0, Math.Max(0, maxLength - 3)), "...");
    }
}