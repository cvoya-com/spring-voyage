// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "unit" command tree for unit management.
/// </summary>
public static class UnitCommand
{
    private static readonly OutputFormatter.Column<UnitResponse>[] UnitColumns =
    {
        new("id", u => u.Id),
        new("name", u => u.Name),
    };

    private static readonly OutputFormatter.Column<UnitMembershipResponse>[] MembershipColumns =
    {
        new("unit", m => m.UnitId),
        new("agent", m => m.AgentAddress),
        new("model", m => m.Model),
        new("specialty", m => m.Specialty),
        new("enabled", m => m.Enabled?.ToString().ToLowerInvariant()),
        new("executionMode", m => m.ExecutionMode?.AgentExecutionMode?.ToString()),
    };

    /// <summary>
    /// Creates the "unit" command with subcommands for CRUD, member operations,
    /// and the cascading purge helper.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var unitCommand = new Command("unit", "Manage units");

        unitCommand.Subcommands.Add(CreateListCommand(outputOption));
        unitCommand.Subcommands.Add(CreateCreateCommand(outputOption));
        unitCommand.Subcommands.Add(CreateDeleteCommand());
        unitCommand.Subcommands.Add(CreatePurgeCommand());
        unitCommand.Subcommands.Add(CreateMembersCommand(outputOption));

        return unitCommand;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var command = new Command("list", "List all units");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListUnitsAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, UnitColumns));
        });

        return command;
    }

    private static Command CreateCreateCommand(Option<string> outputOption)
    {
        // "name" is the unit's address path and unique identifier; the server generates the actor id.
        var nameArg = new Argument<string>("name") { Description = "The unit name (address path; also used as the identifier)" };
        var displayNameOption = new Option<string?>("--display-name") { Description = "Human-readable display name (defaults to name)" };
        var descriptionOption = new Option<string?>("--description") { Description = "Description of the unit's purpose" };
        var command = new Command("create", "Create a new unit");
        command.Arguments.Add(nameArg);
        command.Options.Add(displayNameOption);
        command.Options.Add(descriptionOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.CreateUnitAsync(name, displayName, description, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, UnitColumns));
        });

        return command;
    }

    private static Command CreateDeleteCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The unit identifier" };
        var command = new Command("delete", "Delete a unit");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var client = ClientFactory.Create();

            await client.DeleteUnitAsync(id, ct);
            Console.WriteLine($"Unit '{id}' deleted.");
        });

        return command;
    }

    private static Command CreatePurgeCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The unit identifier" };
        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Required acknowledgement that this cascading delete is intentional",
        };
        var command = new Command(
            "purge",
            "Cascading cleanup: delete every membership row for the unit, then delete the unit itself. Requires --confirm because it is destructive.");
        command.Arguments.Add(idArg);
        command.Options.Add(confirmOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var confirm = parseResult.GetValue(confirmOption);
            if (!confirm)
            {
                await Console.Error.WriteLineAsync(
                    $"Refusing to purge unit '{id}' without --confirm. Re-run with --confirm to proceed.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // Step 1: enumerate memberships so the user sees exactly what is cascading.
            var memberships = await client.ListUnitMembershipsAsync(id, ct);
            Console.WriteLine(
                $"Purging unit '{id}': {memberships.Count} membership(s) to remove before the unit itself.");

            // Step 2: delete each membership row. We fail loud on the first error so
            // the caller can investigate before the unit itself disappears.
            foreach (var membership in memberships)
            {
                var agentAddress = membership.AgentAddress ?? string.Empty;
                Console.WriteLine($"  - removing membership for agent '{agentAddress}'");
                await client.DeleteMembershipAsync(id, agentAddress, ct);
            }

            // Step 3: delete the unit.
            Console.WriteLine($"  - deleting unit '{id}'");
            await client.DeleteUnitAsync(id, ct);
            Console.WriteLine($"Unit '{id}' purged.");
        });

        return command;
    }

    private static Command CreateMembersCommand(Option<string> outputOption)
    {
        var membersCommand = new Command("members", "Manage unit memberships (agents assigned to this unit)");

        membersCommand.Subcommands.Add(CreateMembersListCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersAddCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersConfigCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersRemoveCommand());

        return membersCommand;
    }

    private static Command CreateMembersListCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "list",
            "List every agent that belongs to this unit, with per-membership config overrides.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var memberships = await client.ListUnitMembershipsAsync(unitId, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(memberships)
                : OutputFormatter.FormatTable(memberships, MembershipColumns));
        });

        return command;
    }

    private static Command CreateMembersAddCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var (options, bind) = BuildMembershipOptions();
        var command = new Command(
            "add",
            "Add an agent to this unit (or update its membership config if one already exists; the backend PUT is idempotent).");
        command.Arguments.Add(unitArg);
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
            await InvokeUpsertAsync(parseResult, unitArg, bind, outputOption, ct));

        return command;
    }

    private static Command CreateMembersConfigCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var (options, bind) = BuildMembershipOptions();
        var command = new Command(
            "config",
            "Update per-membership config for an existing agent in this unit. Same underlying upsert as 'add', but semantically signals a configuration change rather than a new assignment.");
        command.Arguments.Add(unitArg);
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
            await InvokeUpsertAsync(parseResult, unitArg, bind, outputOption, ct));

        return command;
    }

    private static Command CreateMembersRemoveCommand()
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var agentOption = new Option<string>("--agent")
        {
            Description = "The agent identifier to remove from this unit",
            Required = true,
        };
        var command = new Command("remove", "Remove an agent's membership from this unit.");
        command.Arguments.Add(unitArg);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var agentId = parseResult.GetValue(agentOption)!;
            var client = ClientFactory.Create();

            await client.DeleteMembershipAsync(unitId, agentId, ct);
            Console.WriteLine($"Membership for agent '{agentId}' removed from unit '{unitId}'.");
        });

        return command;
    }

    /// <summary>
    /// Shared options + parse helper for <c>members add</c> and <c>members config</c>
    /// — both drive the same upsert endpoint with identical flags.
    /// </summary>
    private static (Option[] Options, Func<ParseResult, MembershipInputs> Bind) BuildMembershipOptions()
    {
        var agentOption = new Option<string>("--agent")
        {
            Description = "The agent identifier",
            Required = true,
        };
        var modelOption = new Option<string?>("--model") { Description = "Override the agent's default model for this unit" };
        var specialtyOption = new Option<string?>("--specialty") { Description = "Override the agent's specialty for this unit" };
        var enabledOption = new Option<bool?>("--enabled") { Description = "Enable/disable this membership (true or false)" };
        var executionModeOption = new Option<string?>("--execution-mode") { Description = "Override execution mode (Auto or OnDemand)" };
        executionModeOption.AcceptOnlyFromAmong("Auto", "OnDemand");

        MembershipInputs Bind(ParseResult pr)
        {
            var executionModeRaw = pr.GetValue(executionModeOption);
            AgentExecutionMode? executionMode = executionModeRaw switch
            {
                null => null,
                "Auto" => AgentExecutionMode.Auto,
                "OnDemand" => AgentExecutionMode.OnDemand,
                _ => throw new InvalidOperationException($"Unknown execution mode '{executionModeRaw}'."),
            };
            return new MembershipInputs(
                AgentId: pr.GetValue(agentOption)!,
                Model: pr.GetValue(modelOption),
                Specialty: pr.GetValue(specialtyOption),
                Enabled: pr.GetValue(enabledOption),
                ExecutionMode: executionMode);
        }

        return (new Option[] { agentOption, modelOption, specialtyOption, enabledOption, executionModeOption }, Bind);
    }

    private static async Task InvokeUpsertAsync(
        ParseResult parseResult,
        Argument<string> unitArg,
        Func<ParseResult, MembershipInputs> bind,
        Option<string> outputOption,
        CancellationToken ct)
    {
        var unitId = parseResult.GetValue(unitArg)!;
        var inputs = bind(parseResult);
        var output = parseResult.GetValue(outputOption) ?? "table";
        var client = ClientFactory.Create();

        var result = await client.UpsertMembershipAsync(
            unitId,
            inputs.AgentId,
            inputs.Model,
            inputs.Specialty,
            inputs.Enabled,
            inputs.ExecutionMode,
            ct);

        Console.WriteLine(output == "json"
            ? OutputFormatter.FormatJson(result)
            : OutputFormatter.FormatTable(result, MembershipColumns));
    }

    private sealed record MembershipInputs(
        string AgentId,
        string? Model,
        string? Specialty,
        bool? Enabled,
        AgentExecutionMode? ExecutionMode);
}