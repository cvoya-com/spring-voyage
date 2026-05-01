// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring thread</c> verb family (#452). Three subcommands:
/// <c>list</c> — filtered thread summaries; <c>show &lt;id&gt;</c> — the full
/// thread (summary + ordered events); <c>send --thread &lt;id&gt;</c> — thread a
/// message into an existing thread (deliberately distinct from
/// <c>spring message send</c> which targets an address and implicitly starts a
/// new thread when no id is supplied).
/// </summary>
public static class ThreadCommand
{
    private static readonly OutputFormatter.Column<ThreadSummaryResponse>[] ListColumns =
    {
        new("id", c => c.Id),
        new("status", c => c.Status),
        new("origin", c => c.Origin?.DisplayName ?? c.Origin?.Address ?? string.Empty),
        new("participants", c => FormatParticipants(c.Participants)),
        new("events", c => c.EventCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        new("lastActivity", c => FormatTimestamp(c.LastActivity)),
        new("summary", c => Truncate(c.Summary, 60)),
    };

    /// <summary>
    /// Creates the <c>thread</c> command tree.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("thread", "Inspect and respond to threads");
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateShowCommand(outputOption));
        cmd.Subcommands.Add(CreateSendCommand(outputOption));
        cmd.Subcommands.Add(CreateCloseCommand(outputOption));
        return cmd;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var unitOption = new Option<string?>("--unit") { Description = "Filter by unit name" };
        var agentOption = new Option<string?>("--agent") { Description = "Filter by agent name" };
        var statusOption = new Option<string?>("--status") { Description = "Filter by status (active | completed)" };
        statusOption.AcceptOnlyFromAmong("active", "completed");
        var participantOption = new Option<string?>("--participant")
        {
            Description = "Filter by participant address (scheme://path, e.g. agent://ada)",
        };
        var limitOption = new Option<int?>("--limit") { Description = "Maximum rows to return (default 50)" };

        var command = new Command("list", "List threads with optional filters");
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(statusOption);
        command.Options.Add(participantOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

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

        });

        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The thread id" };
        var command = new Command("show", "Show a thread (summary + ordered events)");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var detail = await client.GetThreadAsync(id, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(detail));
                    return;
                }

                var summary = detail.Summary;
                if (summary is not null)
                {
                    Console.WriteLine($"Thread:       {summary.Id}");
                    Console.WriteLine($"Status:       {summary.Status}");
                    Console.WriteLine($"Origin:       {summary.Origin?.DisplayName ?? summary.Origin?.Address ?? string.Empty}");
                    Console.WriteLine($"Participants: {FormatParticipants(summary.Participants)}");
                    Console.WriteLine($"Created:      {FormatTimestamp(summary.CreatedAt)}");
                    Console.WriteLine($"Last:         {FormatTimestamp(summary.LastActivity)}");
                    Console.WriteLine();
                }

                var events = detail.Events ?? new List<ThreadEventResponse>();
                // #1209: render the message body inline for events that
                // carry one (the activity-projection now stamps the
                // sender / recipient / body on every MessageReceived
                // event). The thread reads top-to-bottom oldest-first so
                // operators can see *what* was said, not just that
                // something was said.
                RenderThreadEvents(events);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to load thread '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateSendCommand(Option<string> outputOption)
    {
        // `spring thread send` requires an existing thread id — that
        // is the whole point of the verb, per #452. `spring message send`
        // already covers the start-a-new-thread path.
        var threadOption = new Option<string>("--thread")
        {
            Description = "The existing thread id to send into.",
            Required = true,
        };
        var addressArg = new Argument<string>("address")
        {
            Description = "Destination address (e.g. agent://engineering-team/ada)",
        };
        var textArg = new Argument<string>("text") { Description = "Message text" };

        var command = new Command(
            "send",
            "Send a message into an existing thread. Use 'spring message send' to start a new thread.");
        command.Options.Add(threadOption);
        command.Arguments.Add(addressArg);
        command.Arguments.Add(textArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var threadId = parseResult.GetValue(threadOption)!;
            var address = parseResult.GetValue(addressArg)!;
            var text = parseResult.GetValue(textArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var (scheme, path) = AddressParser.Parse(address);
            var client = ClientFactory.Create();

            try
            {
                var result = await client.SendThreadMessageAsync(
                    threadId, scheme, path, text, ct: ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : $"Message sent to {address} in thread {result.ThreadId}. (id: {result.MessageId?.ToString() ?? "n/a"})");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to send to thread '{threadId}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateCloseCommand(Option<string> outputOption)
    {
        // #1038: operator-driven close. Required when a dispatch fails in a
        // way the actor itself cannot recover from (e.g. container exit 125
        // before the runtime instance materialised — #1036) so the agent
        // does not stay stuck on a dead thread forever.
        var idArg = new Argument<string>("id") { Description = "The thread id to close" };
        var reasonOption = new Option<string?>("--reason")
        {
            Description = "Optional human-readable reason — surfaced on the ThreadClosed activity event.",
        };

        var command = new Command(
            "close",
            "Close (abort) an in-flight or pending thread across every participating agent.");
        command.Arguments.Add(idArg);
        command.Options.Add(reasonOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var reason = parseResult.GetValue(reasonOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();

            try
            {
                var detail = await client.CloseThreadAsync(id, reason, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(detail));
                    return;
                }

                var summary = detail.Summary;
                Console.WriteLine($"Thread {id} closed.");
                if (summary is not null)
                {
                    Console.WriteLine($"Status:       {summary.Status}");
                    Console.WriteLine($"Participants: {FormatParticipants(summary.Participants ?? [])}");

                    Console.WriteLine($"Last:         {FormatTimestamp(summary.LastActivity)}");
                }
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    Console.WriteLine($"Reason:       {reason}");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to close thread '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    /// <summary>
    /// Renders the ordered event timeline for a thread, inlining
    /// the message body for every <c>MessageReceived</c> event that
    /// carries one (#1209). Error events (#1161) are prefixed with
    /// <c>!!</c> and written to stderr so the operator sees them
    /// immediately and scripted consumers can separate the error signal
    /// from normal output. Other event types fall back to the existing
    /// summary-only row so the timeline stays compact.
    /// </summary>
    public static void RenderThreadEvents(IReadOnlyList<ThreadEventResponse> events)
    {
        if (events.Count == 0)
        {
            Console.WriteLine("(no events yet)");
            return;
        }

        foreach (var evt in events)
        {
            var ts = FormatTimestamp(evt.Timestamp);
            var sourceLabel = evt.Source?.DisplayName ?? evt.Source?.Address ?? string.Empty;

            // #1161: dispatch failures and system errors must be visible
            // inline — the operator should not need to open the activity
            // log to discover that a message failed to dispatch. Write to
            // stderr so the signal is separable from structured output.
            if (string.Equals(evt.EventType, "ErrorOccurred", StringComparison.Ordinal)
                || string.Equals(evt.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[{ts}] !! {evt.Summary}");
                continue;
            }

            if (string.Equals(evt.EventType, "MessageReceived", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(evt.Body))
            {
                var fromRef = evt.From?.ParticipantRef;
                var sender = fromRef?.DisplayName ?? fromRef?.Address ?? sourceLabel;
                var recipient = !string.IsNullOrWhiteSpace(evt.To) ? evt.To : sourceLabel;
                Console.WriteLine($"[{ts}] {sender} -> {recipient}");
                Console.WriteLine(evt.Body);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"[{ts}] [{sourceLabel}] {evt.EventType} — {evt.Summary}");
            }
        }
    }

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

        return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, Math.Max(0, maxLength - 3)), "...");
    }
}