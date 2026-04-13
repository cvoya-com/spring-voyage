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

    /// <summary>
    /// Creates the "agent" command with subcommands for CRUD operations.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var agentCommand = new Command("agent", "Manage agents");

        agentCommand.Subcommands.Add(CreateListCommand(outputOption));
        agentCommand.Subcommands.Add(CreateCreateCommand(outputOption));
        agentCommand.Subcommands.Add(CreateStatusCommand(outputOption));
        agentCommand.Subcommands.Add(CreateDeleteCommand());

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
}