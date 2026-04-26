// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;


/// <summary>
/// Builds the "message" command tree for sending and inspecting messages.
/// </summary>
public static class MessageCommand
{
    /// <summary>
    /// Creates the "message" command with the "send" and "show" subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var messageCommand = new Command("message", "Send and inspect messages");

        messageCommand.Subcommands.Add(CreateSendCommand(outputOption));
        messageCommand.Subcommands.Add(CreateShowCommand(outputOption));

        return messageCommand;
    }

    private static Command CreateSendCommand(Option<string> outputOption)
    {
        var addressArg = new Argument<string>("address") { Description = "Destination address (e.g. agent://ada)" };
        var textArg = new Argument<string>("text") { Description = "Message text" };
        var conversationOption = new Option<string?>("--conversation") { Description = "Conversation identifier" };
        var command = new Command("send", "Send a message to an address");
        command.Arguments.Add(addressArg);
        command.Arguments.Add(textArg);
        command.Options.Add(conversationOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var address = parseResult.GetValue(addressArg)!;
            var text = parseResult.GetValue(textArg)!;
            var conversationId = parseResult.GetValue(conversationOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var verbose = parseResult.GetValue<bool>("--verbose");
            var (scheme, path) = AddressParser.Parse(address);
            var client = ClientFactory.Create();

            var result = await client.SendMessageAsync(scheme, path, text, conversationId, ct);

            // #985: surface the resolved conversation id so operators can
            // thread follow-up sends. The server auto-generates one when the
            // caller omits `--conversation` on Domain messages to agent://
            // targets; echo it either way so the CLI behaviour is uniform.
            var messageIdText = result.MessageId?.ToString() ?? "n/a";
            var conversationIdText = !string.IsNullOrWhiteSpace(result.ConversationId)
                ? result.ConversationId
                : "n/a";

            // #1064: pass `verbose` so the Kiota → System.Text.Json fallback
            // surfaces a one-line warning when it kicks in, while keeping
            // scripted output clean by default.
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result, verbose)
                : $"Sent message {messageIdText} to {address} in conversation {conversationIdText}.");
        });

        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("message-id")
        {
            Description = "The message id (GUID) to show",
        };
        var command = new Command(
            "show",
            "Show the body and envelope of a single message by id");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var raw = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!Guid.TryParse(raw, out var messageId))
            {
                await Console.Error.WriteLineAsync(
                    $"'{raw}' is not a valid message id (expected a GUID).");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            try
            {
                var detail = await client.GetMessageAsync(messageId, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(detail));
                    return;
                }

                Console.WriteLine($"Message:      {detail.MessageId}");
                if (!string.IsNullOrWhiteSpace(detail.ConversationId))
                {
                    Console.WriteLine($"Conversation: {detail.ConversationId}");
                }
                Console.WriteLine($"Type:         {detail.MessageType}");
                Console.WriteLine($"From:         {detail.From}");
                Console.WriteLine($"To:           {detail.To}");
                if (detail.Timestamp is DateTimeOffset ts)
                {
                    Console.WriteLine($"Timestamp:    {ts:yyyy-MM-dd HH:mm:ss}");
                }
                Console.WriteLine();

                if (!string.IsNullOrEmpty(detail.Body))
                {
                    Console.WriteLine(detail.Body);
                }
                else if (detail.Payload is not null)
                {
                    // Non-text payload — point operators at the JSON view
                    // since Kiota wraps the polymorphic payload in a union
                    // type that doesn't render cleanly in plain text.
                    Console.WriteLine("(structured payload — re-run with --output json to inspect)");
                }
                else
                {
                    Console.WriteLine("(no body)");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to load message '{messageId}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }
}