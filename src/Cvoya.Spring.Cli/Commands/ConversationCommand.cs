// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring conversation</c> verb family (#452). Three subcommands:
/// <c>list</c> — filtered conversation summaries; <c>show &lt;id&gt;</c> — the full
/// thread (summary + ordered events); <c>send --conversation &lt;id&gt;</c> — thread a
/// message into an existing conversation (deliberately distinct from
/// <c>spring message send</c> which targets an address and implicitly starts a
/// new conversation when no id is supplied).
/// </summary>
public static class ConversationCommand
{
    private static readonly OutputFormatter.Column<ConversationSummary>[] ListColumns =
    {
        new("id", c => c.Id),
        new("status", c => c.Status),
        new("origin", c => c.Origin),
        new("participants", c => FormatParticipants(c.Participants)),
        new("events", c => UntypedNodeFormatter.FormatScalar(c.EventCount)),
        new("lastActivity", c => FormatTimestamp(c.LastActivity)),
        new("summary", c => Truncate(c.Summary, 60)),
    };

    private static readonly OutputFormatter.Column<ConversationEvent>[] EventColumns =
    {
        new("timestamp", e => FormatTimestamp(e.Timestamp)),
        new("source", e => e.Source),
        new("type", e => e.EventType),
        new("severity", e => e.Severity),
        new("summary", e => Truncate(e.Summary, 80)),
    };

    /// <summary>
    /// Creates the <c>conversation</c> command tree.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("conversation", "Inspect and respond to conversations");
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateShowCommand(outputOption));
        cmd.Subcommands.Add(CreateSendCommand(outputOption));
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

        var command = new Command("list", "List conversations with optional filters");
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(statusOption);
        command.Options.Add(participantOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListConversationsAsync(
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
        var idArg = new Argument<string>("id") { Description = "The conversation id" };
        var command = new Command("show", "Show a conversation thread (summary + ordered events)");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var detail = await client.GetConversationAsync(id, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(detail));
                    return;
                }

                var summary = detail.Summary;
                if (summary is not null)
                {
                    Console.WriteLine($"Conversation: {summary.Id}");
                    Console.WriteLine($"Status:       {summary.Status}");
                    Console.WriteLine($"Origin:       {summary.Origin}");
                    Console.WriteLine($"Participants: {FormatParticipants(summary.Participants)}");
                    Console.WriteLine($"Created:      {FormatTimestamp(summary.CreatedAt)}");
                    Console.WriteLine($"Last:         {FormatTimestamp(summary.LastActivity)}");
                    Console.WriteLine();
                }

                var events = detail.Events ?? new List<ConversationEvent>();
                Console.WriteLine(OutputFormatter.FormatTable(events, EventColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to load conversation '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateSendCommand(Option<string> outputOption)
    {
        // `spring conversation send` requires an existing conversation id — that
        // is the whole point of the verb, per #452. `spring message send`
        // already covers the start-a-new-conversation path.
        var conversationOption = new Option<string>("--conversation")
        {
            Description = "The existing conversation id to thread into.",
            Required = true,
        };
        var addressArg = new Argument<string>("address")
        {
            Description = "Destination address (e.g. agent://engineering-team/ada)",
        };
        var textArg = new Argument<string>("text") { Description = "Message text" };

        var command = new Command(
            "send",
            "Send a message into an existing conversation. Use 'spring message send' to start a new conversation.");
        command.Options.Add(conversationOption);
        command.Arguments.Add(addressArg);
        command.Arguments.Add(textArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var conversationId = parseResult.GetValue(conversationOption)!;
            var address = parseResult.GetValue(addressArg)!;
            var text = parseResult.GetValue(textArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var (scheme, path) = AddressParser.Parse(address);
            var client = ClientFactory.Create();

            try
            {
                var result = await client.SendConversationMessageAsync(
                    conversationId, scheme, path, text, ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : $"Message sent to {address} in conversation {result.ConversationId}. (id: {result.MessageId?.ToString() ?? "n/a"})");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to send to conversation '{conversationId}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static string FormatParticipants(IEnumerable<string>? participants)
    {
        if (participants is null)
        {
            return string.Empty;
        }

        var list = participants.ToList();
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