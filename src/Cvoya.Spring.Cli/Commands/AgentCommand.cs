// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "agent" command tree for agent management.
/// </summary>
public static class AgentCommand
{
    private static readonly OutputFormatter.Column<AgentResponse>[] AgentListColumns =
    {
        new("id", a => a.Id),
        new("name", a => a.Name),
        new("role", a => a.Role),
        new("enabled", a => a.Enabled?.ToString().ToLowerInvariant()),
    };

    private static readonly OutputFormatter.Column<AgentResponse>[] AgentCreateColumns =
    {
        new("id", a => a.Id),
        new("name", a => a.Name),
        new("role", a => a.Role),
    };

    private static readonly OutputFormatter.Column<AgentDetailResponse>[] AgentStatusColumns =
    {
        new("id", a => a.Agent?.Id),
        new("name", a => a.Agent?.Name),
        new("enabled", a => a.Agent?.Enabled?.ToString().ToLowerInvariant()),
    };

    private sealed record CloneRow(
        string CloneId,
        string Parent,
        string CloneType,
        string AttachmentMode,
        string Status,
        string CreatedAt,
        string? LocalAlias);

    private static readonly OutputFormatter.Column<CloneRow>[] CloneColumns =
    {
        new("cloneId", r => r.CloneId),
        new("parent", r => r.Parent),
        new("cloneType", r => r.CloneType),
        new("attachmentMode", r => r.AttachmentMode),
        new("status", r => r.Status),
        new("createdAt", r => r.CreatedAt),
        new("alias", r => r.LocalAlias),
    };

    /// <summary>
    /// Creates the "agent" command with subcommands for CRUD operations,
    /// the cascading purge helper (#320), and the clone surface (#458).
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var agentCommand = new Command("agent", "Manage agents");

        agentCommand.Subcommands.Add(CreateListCommand(outputOption));
        agentCommand.Subcommands.Add(CreateCreateCommand(outputOption));
        agentCommand.Subcommands.Add(CreateStatusCommand(outputOption));
        agentCommand.Subcommands.Add(CreateDeleteCommand());
        agentCommand.Subcommands.Add(CreatePurgeCommand());
        agentCommand.Subcommands.Add(CreateCloneCommand(outputOption));

        return agentCommand;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var command = new Command("list", "List all agents");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListAgentsAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, AgentListColumns));
        });

        return command;
    }

    private static Command CreateCreateCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier (sent as the server's Name field)" };
        var nameOption = new Option<string?>("--name") { Description = "Human-readable display name (defaults to id)" };
        var roleOption = new Option<string?>("--role") { Description = "The agent role" };
        var command = new Command("create", "Create a new agent");
        command.Arguments.Add(idArg);
        command.Options.Add(nameOption);
        command.Options.Add(roleOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var displayName = parseResult.GetValue(nameOption);
            var role = parseResult.GetValue(roleOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.CreateAgentAsync(id, displayName, role, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, AgentCreateColumns));
        });

        return command;
    }

    private static Command CreateStatusCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var command = new Command("status", "Get agent status");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.GetAgentStatusAsync(id, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, AgentStatusColumns));
        });

        return command;
    }

    private static Command CreateDeleteCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var command = new Command("delete", "Delete an agent");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var client = ClientFactory.Create();

            await client.DeleteAgentAsync(id, ct);
            Console.WriteLine($"Agent '{id}' deleted.");
        });

        return command;
    }

    private static Command CreatePurgeCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Required acknowledgement that this cascading delete is intentional",
        };
        var command = new Command(
            "purge",
            "Cascading cleanup: remove every membership this agent has, then delete the agent itself. Requires --confirm because it is destructive.");
        command.Arguments.Add(idArg);
        command.Options.Add(confirmOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var confirm = parseResult.GetValue(confirmOption);
            if (!confirm)
            {
                await Console.Error.WriteLineAsync(
                    $"Refusing to purge agent '{id}' without --confirm. Re-run with --confirm to proceed.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // Step 1: enumerate memberships so users see exactly what is cascading.
            var memberships = await client.ListAgentMembershipsAsync(id, ct);
            Console.WriteLine(
                $"Purging agent '{id}': {memberships.Count} membership(s) to remove before the agent itself.");

            // Step 2: remove the agent from each unit it belongs to. Fail loud on the
            // first error so the caller can investigate before the agent is deleted.
            foreach (var membership in memberships)
            {
                var unitId = membership.UnitId ?? string.Empty;
                Console.WriteLine($"  - removing membership from unit '{unitId}'");
                await client.DeleteMembershipAsync(unitId, id, ct);
            }

            // Step 3: delete the agent record.
            Console.WriteLine($"  - deleting agent '{id}'");
            await client.DeleteAgentAsync(id, ct);
            Console.WriteLine($"Agent '{id}' purged.");
        });

        return command;
    }

    // Clone subcommand tree (#458) — mirrors the portal's Create/List clone
    // actions so CLI and UI stay at parity. Clone identity comes from the
    // server (a new GUID); --name is a local alias echoed back in table/JSON
    // output so callers can tag a clone when they script provisioning, but
    // it is not persisted because the server contract today has no clone
    // name field. When PR-PLAT-CLONE-1 adds persistent naming we swap the
    // local alias in for the server-side field without changing the flag.
    private static Command CreateCloneCommand(Option<string> outputOption)
    {
        var cloneCommand = new Command("clone", "Manage agent clones");
        cloneCommand.Subcommands.Add(CreateCloneCreateCommand(outputOption));
        cloneCommand.Subcommands.Add(CreateCloneListCommand(outputOption));
        return cloneCommand;
    }

    private static Command CreateCloneCreateCommand(Option<string> outputOption)
    {
        var agentOption = new Option<string>("--agent")
        {
            Description = "The parent agent's identifier.",
            Required = true,
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Optional local alias for the clone, echoed in CLI output. The server assigns the canonical clone id.",
        };
        var cloneTypeOption = new Option<string>("--clone-type")
        {
            Description = "Cloning policy: none, ephemeral-no-memory (default), or ephemeral-with-memory. Matches the portal's Clone type dropdown.",
            DefaultValueFactory = _ => "ephemeral-no-memory",
        };
        cloneTypeOption.AcceptOnlyFromAmong("none", "ephemeral-no-memory", "ephemeral-with-memory");

        var attachmentOption = new Option<string>("--attachment-mode")
        {
            Description = "Attachment mode: detached (default) or attached. Matches the portal's Attachment mode dropdown.",
            DefaultValueFactory = _ => "detached",
        };
        attachmentOption.AcceptOnlyFromAmong("detached", "attached");

        var command = new Command("create", "Create a clone of an agent (same contract as the portal's Create clone action)");
        command.Options.Add(agentOption);
        command.Options.Add(nameOption);
        command.Options.Add(cloneTypeOption);
        command.Options.Add(attachmentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agent = parseResult.GetValue(agentOption)!;
            var alias = parseResult.GetValue(nameOption);
            var cloneTypeRaw = parseResult.GetValue(cloneTypeOption) ?? "ephemeral-no-memory";
            var attachmentRaw = parseResult.GetValue(attachmentOption) ?? "detached";
            var output = parseResult.GetValue(outputOption) ?? "table";

            var cloneType = cloneTypeRaw switch
            {
                "none" => CloningPolicy.None,
                "ephemeral-no-memory" => CloningPolicy.EphemeralNoMemory,
                "ephemeral-with-memory" => CloningPolicy.EphemeralWithMemory,
                _ => throw new InvalidOperationException($"Unexpected clone type '{cloneTypeRaw}'."),
            };
            var attachmentMode = attachmentRaw switch
            {
                "detached" => AttachmentMode.Detached,
                "attached" => AttachmentMode.Attached,
                _ => throw new InvalidOperationException($"Unexpected attachment mode '{attachmentRaw}'."),
            };

            var client = ClientFactory.Create();
            var result = await client.CreateCloneAsync(agent, cloneType, attachmentMode, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var row = ToCloneRow(result, alias);
                Console.WriteLine(OutputFormatter.FormatTable(row, CloneColumns));
            }
        });

        return command;
    }

    private static Command CreateCloneListCommand(Option<string> outputOption)
    {
        var agentOption = new Option<string>("--agent")
        {
            Description = "The parent agent's identifier.",
            Required = true,
        };
        var command = new Command("list", "List the clones of an agent");
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agent = parseResult.GetValue(agentOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var clones = await client.ListClonesAsync(agent, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(clones));
            }
            else
            {
                var rows = new List<CloneRow>();
                foreach (var clone in clones)
                {
                    rows.Add(ToCloneRow(clone, alias: null));
                }

                Console.WriteLine(OutputFormatter.FormatTable(rows, CloneColumns));
            }
        });

        return command;
    }

    private static CloneRow ToCloneRow(CloneResponse response, string? alias) =>
        new(
            response.CloneId ?? string.Empty,
            response.ParentAgentId ?? string.Empty,
            response.CloneType?.ToString() ?? string.Empty,
            response.AttachmentMode?.ToString() ?? string.Empty,
            response.Status ?? string.Empty,
            response.CreatedAt is System.DateTimeOffset dto
                ? dto.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            alias);
}