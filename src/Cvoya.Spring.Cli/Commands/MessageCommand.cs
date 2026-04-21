// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "message" command tree for sending messages.
/// </summary>
public static class MessageCommand
{
    /// <summary>
    /// Creates the "message" command with the "send" subcommand.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var messageCommand = new Command("message", "Send and manage messages");

        messageCommand.Subcommands.Add(CreateSendCommand(outputOption));

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

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : $"Sent message {messageIdText} to {address} in conversation {conversationIdText}.");
        });

        return command;
    }
}