// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring inbox</c> verb family (#456). The inbox is the
/// "awaiting me" surface described in <c>docs/design/portal-exploration.md</c>
/// § 3.4: threads whose most recent event targeted the caller's
/// <c>human://</c> address and where the caller has not yet replied.
/// <para>
/// <c>list</c> enumerates rows; <c>show &lt;id&gt;</c> is a thin alias over
/// <see cref="ThreadCommand"/>'s <c>show</c> (same thread view, just
/// reachable from the inbox verb so operators don't have to switch verbs mid-
/// flow); <c>respond &lt;id&gt;</c> is the corresponding thin wrapper over
/// <see cref="ThreadCommand"/>'s <c>send</c>.
/// </para>
/// </summary>
public static class InboxCommand
{
    private static readonly OutputFormatter.Column<InboxItemResponse>[] ListColumns =
    {
        new("thread", r => r.ThreadId),
        new("from", r => r.From?.DisplayName ?? r.From?.Address ?? string.Empty),
        new("human", r => r.Human?.DisplayName ?? r.Human?.Address ?? string.Empty),
        new("pendingSince", r => FormatTimestamp(r.PendingSince)),
        new("summary", r => Truncate(r.Summary, 80)),
    };

    /// <summary>
    /// Creates the <c>inbox</c> command tree.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("inbox", "Inspect and respond to threads awaiting the current human");
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateShowCommand(outputOption));
        cmd.Subcommands.Add(CreateRespondCommand(outputOption));
        return cmd;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var command = new Command("list", "List threads awaiting a response from the current human");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var items = await client.ListInboxAsync(ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(items));
                return;
            }

            if (items.Count == 0)
            {
                Console.WriteLine("Inbox is empty.");
                return;
            }

            Console.WriteLine(OutputFormatter.FormatTable(items, ListColumns));
        });

        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("thread-id")
        {
            Description = "The thread id of the inbox row to open",
        };
        var command = new Command(
            "show",
            "Show an inbox item — the thread pending a response. Alias for `spring thread show`.");
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
                    Console.WriteLine($"Inbox item:   {summary.Id}");
                    Console.WriteLine($"Origin:       {summary.Origin?.DisplayName ?? summary.Origin?.Address ?? string.Empty}");
                    Console.WriteLine($"Status:       {summary.Status}");
                    Console.WriteLine($"Last activity: {FormatTimestamp(summary.LastActivity)}");
                    Console.WriteLine();
                }

                // #1209: thin alias of `spring thread show` — share
                // the renderer so message bodies surface inline on inbox
                // show too.
                var events = detail.Events ?? new List<ThreadEventResponse>();
                ThreadCommand.RenderThreadEvents(events);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to load inbox item '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateRespondCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("thread-id")
        {
            Description = "The thread id to respond to",
        };
        var addressOption = new Option<string?>("--to")
        {
            Description =
                "Destination address (e.g. agent://engineering-team/ada). "
                + "Optional — when omitted the reply goes to the sender of the pending ask.",
        };
        var textArg = new Argument<string>("text") { Description = "Reply text" };

        var command = new Command(
            "respond",
            "Reply to an inbox thread. Thin wrapper over `spring thread send --thread <id>`.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(textArg);
        command.Options.Add(addressOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var text = parseResult.GetValue(textArg)!;
            var addressOverride = parseResult.GetValue(addressOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            // When --to is omitted we resolve the requester from the inbox
            // itself so `respond <id> "..."` works with no extra bookkeeping.
            string targetAddress;
            if (!string.IsNullOrWhiteSpace(addressOverride))
            {
                targetAddress = addressOverride!;
            }
            else
            {
                var inbox = await client.ListInboxAsync(ct);
                var match = inbox.FirstOrDefault(i =>
                    string.Equals(i.ThreadId, id, StringComparison.Ordinal));
                var fromAddress = match?.From?.Address;
                if (match is null || string.IsNullOrEmpty(fromAddress))
                {
                    await Console.Error.WriteLineAsync(
                        $"No inbox row found for thread '{id}'. Pass --to <address> to force a reply target.");
                    Environment.Exit(1);
                    return;
                }
                targetAddress = fromAddress!;
            }

            var (scheme, path) = AddressParser.Parse(targetAddress);

            try
            {
                var result = await client.SendThreadMessageAsync(id, scheme, path, text, ct: ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : $"Replied to {targetAddress} in thread {result.ThreadId}. (id: {result.MessageId?.ToString() ?? "n/a"})");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to respond to '{id}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
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