// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
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
        var addressArg = new Argument<string>("address") { Description = "Destination address in canonical form scheme:<guid> (e.g. agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7)" };
        var textArg = new Argument<string>("text") { Description = "Message text" };
        var conversationOption = new Option<string?>("--thread") { Description = "Thread identifier" };
        // ADR-0062 § 3 / § 6: explicit "speaking-as" Hat. The CLI resolves
        // display-name input (#2827) against the calling caller's bound-
        // Hat set via GET /api/v1/tenant/users/me/humans before sending;
        // bare Guids pass through without a round-trip.
        var asOption = new Option<string?>("--as")
        {
            Description =
                "Explicit 'speaking-as' Hat. Accepts the Hat UUID (dashed or no-dash) or a Hat " +
                "display name (case-insensitive, must be unambiguous within your bound Hats). " +
                "Defaults to your primary Hat per ADR-0062 § 3 when omitted. An unbound Hat " +
                "returns a CLI-friendly 400.",
        };
        var command = new Command("send", "Send a message to an address");
        command.Arguments.Add(addressArg);
        command.Arguments.Add(textArg);
        command.Options.Add(conversationOption);
        command.Options.Add(asOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var address = parseResult.GetValue(addressArg)!;
            var text = parseResult.GetValue(textArg)!;
            var threadInput = parseResult.GetValue(conversationOption);
            var asInput = parseResult.GetValue(asOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var verbose = parseResult.GetValue<bool>("--verbose");
            var (scheme, path) = AddressParser.Parse(address);
            var client = ClientFactory.Create();

            // Resolve the path through CliResolver for agent / unit schemes so
            // the wire body's recipient id is the canonical no-dash hex form
            // the dispatcher requires. human / connector / system addresses
            // stay opaque — they aren't tenant entities.
            if (!string.IsNullOrWhiteSpace(path)
                && (scheme == "agent" || scheme == "unit"))
            {
                var resolver = new CliResolver(client);
                try
                {
                    path = scheme == "agent"
                        ? await resolver.ResolveAgentIdAsync(path, unitContext: null, ct)
                        : await resolver.ResolveUnitIdAsync(path, parentContext: null, ct);
                }
                catch (CliResolutionException ex)
                {
                    CliResolutionPrinter.Write(Console.Error, ex);
                    Environment.Exit(1);
                    return;
                }
            }

            // Threads aren't in CliResolver; normalise the id when it parses
            // so the wire body carries the canonical no-dash form.
            var threadId = !string.IsNullOrWhiteSpace(threadInput) && Guid.TryParse(threadInput, out var parsedThreadId)
                ? Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parsedThreadId)
                : threadInput;

            // #2972: scope the --as resolution to the Hats that can reach
            // the destination. Only unit/agent targets are gated; other
            // schemes resolve against the full bound set.
            var targetRecipient = scheme is "agent" or "unit"
                ? $"{scheme}:{path}"
                : null;

            Guid? fromHumanId = null;
            if (!string.IsNullOrWhiteSpace(asInput))
            {
                // ADR-0062 § 6 / #2827: accept the Hat UUID or the
                // display name. RefResolver short-circuits on bare
                // Guids (no round-trip) and falls back to /me/humans
                // for the name path. #2972: pass the destination so the
                // name match + ambiguity prompt only offer wearable Hats.
                try
                {
                    fromHumanId = await RefResolver.ResolveHumanRefAsync(
                        client, asInput, "--as", targetRecipient, ct);
                }
                catch (CliRefResolutionException ex)
                {
                    await Console.Error.WriteLineAsync(ex.Message);
                    Environment.Exit(1);
                    return;
                }
            }

            MessageResponse result;
            try
            {
                result = await client.SendMessageAsync(scheme, path, text, threadId, fromHumanId, ct);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
                when (ex.ResponseStatusCode == 400 && HasProblemCode(ex, "NoBoundHuman"))
            {
                var refDisplay = asInput ?? "(default)";
                await Console.Error.WriteLineAsync(
                    $"Hat '{refDisplay}' is not bound to your user. " +
                    "Run `spring user identity list` to see your bound Hats.");
                Environment.Exit(1);
                return;
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
                when (ex.ResponseStatusCode == 403
                    && (HasProblemCode(ex, "NoReachableHat") || HasProblemCode(ex, "HatCannotReachTarget")))
            {
                // #2972: the Hat ↔ unit reachability gate rejected the send —
                // either no Hat the operator wears can reach the target, or
                // the explicit --as Hat cannot. Surface the server's detail
                // (it already explains the rule) with a fallback.
                var detail = ProblemDetailsTranslator.Format(ex);
                await Console.Error.WriteLineAsync(string.IsNullOrWhiteSpace(detail)
                    ? $"You have no Hat that can message {address}. A unit or agent is " +
                      "reachable only through a human member of the unit it belongs to."
                    : detail);
                Environment.Exit(1);
                return;
            }

            // #985: surface the resolved thread id so operators can
            // thread follow-up sends. The server auto-generates one when the
            // caller omits `--thread` on Domain messages to agent://
            // targets; echo it either way so the CLI behaviour is uniform.
            var messageIdText = result.MessageId?.ToString() ?? "n/a";
            var threadIdText = !string.IsNullOrWhiteSpace(result.ThreadId)
                ? result.ThreadId
                : "n/a";

            // #1064: pass `verbose` so the Kiota → System.Text.Json fallback
            // surfaces a one-line warning when it kicks in, while keeping
            // scripted output clean by default.
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result, verbose)
                : $"Sent message {messageIdText} to {address} in thread {threadIdText}.");
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
                if (!string.IsNullOrWhiteSpace(detail.ThreadId))
                {
                    Console.WriteLine($"Thread:       {detail.ThreadId}");
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
                    $"Failed to load message '{messageId}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    /// <summary>
    /// Recognises a structured ProblemDetails <c>code</c> extension on a
    /// Kiota <see cref="Microsoft.Kiota.Abstractions.ApiException"/>. The
    /// message-send handler emits the ADR-0062 § 3 <c>NoBoundHuman</c> (400)
    /// and the #2972 reachability codes <c>NoReachableHat</c> /
    /// <c>HatCannotReachTarget</c> (403) this way. Kiota surfaces the
    /// extension on <c>AdditionalData["code"]</c> as a schema-less
    /// passthrough — a <see cref="System.Text.Json.JsonElement"/> on the
    /// typed path, or a raw string on older paths; cover both.
    /// </summary>
    private static bool HasProblemCode(Microsoft.Kiota.Abstractions.ApiException ex, string code)
    {
        if (ex is not ProblemDetails problem || problem.AdditionalData is null)
        {
            return false;
        }
        if (!problem.AdditionalData.TryGetValue("code", out var raw))
        {
            return false;
        }
        return raw switch
        {
            string s => string.Equals(s, code, StringComparison.Ordinal),
            System.Text.Json.JsonElement el =>
                el.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(el.GetString(), code, StringComparison.Ordinal),
            _ => false,
        };
    }
}
