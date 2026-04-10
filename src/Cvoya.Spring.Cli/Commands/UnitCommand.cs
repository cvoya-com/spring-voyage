// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "unit" command tree for unit management.
/// </summary>
public static class UnitCommand
{
    /// <summary>
    /// Creates the "unit" command with subcommands for CRUD and member operations.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var unitCommand = new Command("unit", "Manage units");

        unitCommand.Subcommands.Add(CreateListCommand(outputOption));
        unitCommand.Subcommands.Add(CreateCreateCommand(outputOption));
        unitCommand.Subcommands.Add(CreateDeleteCommand());
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
                : OutputFormatter.FormatTable(result, ["id", "name"]));
        });

        return command;
    }

    private static Command CreateCreateCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The unit identifier" };
        var nameOption = new Option<string>("--name") { Description = "The unit display name", Required = true };
        var command = new Command("create", "Create a new unit");
        command.Arguments.Add(idArg);
        command.Options.Add(nameOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var name = parseResult.GetValue(nameOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.CreateUnitAsync(id, name, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ["id", "name"]));
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

    private static Command CreateMembersCommand(Option<string> outputOption)
    {
        var membersCommand = new Command("members", "Manage unit members");

        membersCommand.Subcommands.Add(CreateMembersAddCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersRemoveCommand());

        return membersCommand;
    }

    private static Command CreateMembersAddCommand(Option<string> outputOption)
    {
        var unitIdArg = new Argument<string>("unitId") { Description = "The unit identifier" };
        var memberAddressArg = new Argument<string>("memberAddress") { Description = "Member address (e.g. agent://ada)" };
        var command = new Command("add", "Add a member to a unit");
        command.Arguments.Add(unitIdArg);
        command.Arguments.Add(memberAddressArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitIdArg)!;
            var memberAddress = parseResult.GetValue(memberAddressArg)!;
            var (scheme, path) = AddressParser.Parse(memberAddress);
            var client = ClientFactory.Create();

            await client.AddMemberAsync(unitId, scheme, path, ct);
            Console.WriteLine($"Member '{memberAddress}' added to unit '{unitId}'.");
        });

        return command;
    }

    private static Command CreateMembersRemoveCommand()
    {
        var unitIdArg = new Argument<string>("unitId") { Description = "The unit identifier" };
        var memberIdArg = new Argument<string>("memberId") { Description = "The member identifier" };
        var command = new Command("remove", "Remove a member from a unit");
        command.Arguments.Add(unitIdArg);
        command.Arguments.Add(memberIdArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitIdArg)!;
            var memberId = parseResult.GetValue(memberIdArg)!;
            var client = ClientFactory.Create();

            await client.RemoveMemberAsync(unitId, memberId, ct);
            Console.WriteLine($"Member '{memberId}' removed from unit '{unitId}'.");
        });

        return command;
    }
}
