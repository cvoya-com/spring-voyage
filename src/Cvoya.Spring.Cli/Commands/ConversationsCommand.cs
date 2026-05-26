// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring conversations</c> verb family (#2787). Conversations
/// are the tenant-wide read-only view over every thread between units and
/// agents — including ones the caller is not a participant of. The verbs
/// here are the CLI equivalent of the portal's <c>/conversations</c> page.
///
/// <para>
/// The endpoints below are gated by the <see cref="Cvoya.Spring.Core.Security.PlatformRoles.TenantObserver"/>
/// role. OSS deployments grant every authenticated caller every role, so
/// the OSS default tenant user has access; cloud deployments scope the
/// grant per identity.
/// </para>
///
/// Subcommands:
/// <list type="bullet">
///   <item><c>list</c> — list every conversation in the tenant; optional unit/agent/participant narrowing</item>
///   <item><c>show &lt;id&gt;</c> — show a single conversation's summary + ordered events</item>
/// </list>
///
/// <para>
/// Read-only: there is no <c>send</c> or <c>answer</c> verb here. To message
/// into a thread the caller must hold <c>TenantUser</c> and go through
/// <c>spring engagement send</c> / <c>spring engagement answer</c>.
/// </para>
/// </summary>
public static class ConversationsCommand
{
    private static readonly OutputFormatter.Column<ThreadSummaryResponse>[] ListColumns =
    {
        new("id", c => c.Id),
        new("participants", c => FormatParticipants(c.Participants)),
        new("events", c => c.EventCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        new("lastActivity", c => FormatTimestamp(c.LastActivity)),
        new("summary", c => Truncate(c.Summary, 60)),
    };

    private static readonly OutputFormatter.Column<ThreadEventResponse>[] DetailColumns =
    {
        new("timestamp", e => FormatTimestamp(e.Timestamp)),
        new("source", e => e.Source?.DisplayName ?? e.Source?.Address ?? string.Empty),
        new("type", e => e.EventType ?? string.Empty),
        new("summary", e => Truncate(e.Summary, 80)),
    };

    /// <summary>
    /// Creates the <c>conversations</c> command tree.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command(
            "conversations",
            "Observe every conversation thread in the tenant (read-only, requires TenantObserver role)");
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateShowCommand(outputOption));
        return cmd;
    }

    // -----------------------------------------------------------------------
    // conversations list
    // -----------------------------------------------------------------------

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var unitOption = new Option<string?>("--unit")
        {
            Description = "Narrow to conversations involving this unit (id or slug)",
        };
        var agentOption = new Option<string?>("--agent")
        {
            Description = "Narrow to conversations involving this agent (id or slug)",
        };
        var participantOption = new Option<string?>("--participant")
        {
            Description = "Filter by participant address in canonical scheme:<guid> form (e.g. human:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7)",
        };
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum rows to return (default 50)",
        };

        var command = new Command(
            "list",
            "List every conversation in the tenant. Returns threads regardless of caller participation — gated by TenantObserver.");
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(participantOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitInput = parseResult.GetValue(unitOption);
            var agentInput = parseResult.GetValue(agentOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string? unitId = null;
            string? agentId = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(unitInput))
                {
                    unitId = await resolver.ResolveUnitIdAsync(unitInput, parentContext: null, ct);
                }
                if (!string.IsNullOrWhiteSpace(agentInput))
                {
                    agentId = await resolver.ResolveAgentIdAsync(agentInput, unitContext: null, ct);
                }
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            try
            {
                var result = await client.ListConversationsAsync(
                    unit: unitId,
                    agent: agentId,
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
                    $"Failed to list conversations: {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // conversations show <id>
    // -----------------------------------------------------------------------

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The conversation (thread) id to inspect" };

        var command = new Command(
            "show",
            "Show a single conversation's summary and ordered events (read-only).");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idInput = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var id = Guid.TryParse(idInput, out var parsedThreadId)
                ? Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parsedThreadId)
                : idInput;

            try
            {
                var detail = await client.GetConversationAsync(id, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(detail));
                    return;
                }

                var summary = detail.Summary;
                Console.WriteLine($"Conversation: {summary?.Id ?? idInput}");
                Console.WriteLine($"Participants: {FormatParticipants(summary?.Participants)}");
                if (summary?.LastActivity is DateTimeOffset last)
                {
                    Console.WriteLine($"Last activity: {FormatTimestamp(last)}");
                }
                if (!string.IsNullOrEmpty(summary?.Summary))
                {
                    Console.WriteLine($"Summary: {summary.Summary}");
                }
                Console.WriteLine();

                var events = detail.Events ?? new List<ThreadEventResponse>();
                if (events.Count == 0)
                {
                    Console.WriteLine("No events recorded on this conversation.");
                    return;
                }

                Console.WriteLine(OutputFormatter.FormatTable(events, DetailColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to load conversation '{idInput}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // -----------------------------------------------------------------------
    // Private helpers (mirrors EngagementCommand helpers)
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
