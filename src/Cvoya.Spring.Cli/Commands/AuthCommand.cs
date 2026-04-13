// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "auth" command tree for authentication management.
/// </summary>
public static class AuthCommand
{
    private static readonly OutputFormatter.Column<CreateTokenResponse>[] CreateTokenColumns =
    {
        new("name", t => t.Name),
        new("token", t => t.Token),
    };

    private static readonly OutputFormatter.Column<TokenResponse>[] ListTokenColumns =
    {
        new("name", t => t.Name),
        new("createdAt", t => t.CreatedAt?.ToString("O")),
    };

    /// <summary>
    /// Creates the "auth" command with subcommands for token management.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var authCommand = new Command("auth", "Manage authentication");

        authCommand.SetAction((ParseResult parseResult) =>
        {
            var config = CliConfig.Load();
            var status = config.ApiToken is not null ? "Authenticated" : "Not authenticated";
            Console.WriteLine($"Endpoint: {config.Endpoint}");
            Console.WriteLine($"Status:   {status}");
        });

        var tokenCommand = new Command("token", "Manage API tokens");

        tokenCommand.Subcommands.Add(CreateTokenCreateCommand(outputOption));
        tokenCommand.Subcommands.Add(CreateTokenListCommand(outputOption));
        tokenCommand.Subcommands.Add(CreateTokenRevokeCommand());

        authCommand.Subcommands.Add(tokenCommand);

        return authCommand;
    }

    private static Command CreateTokenCreateCommand(Option<string> outputOption)
    {
        var nameArg = new Argument<string>("name") { Description = "Name of the token to create" };
        var command = new Command("create", "Create a new API token");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.CreateTokenAsync(name, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, CreateTokenColumns));
        });

        return command;
    }

    private static Command CreateTokenListCommand(Option<string> outputOption)
    {
        var command = new Command("list", "List API tokens");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListTokensAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ListTokenColumns));
        });

        return command;
    }

    private static Command CreateTokenRevokeCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "Name of the token to revoke" };
        var command = new Command("revoke", "Revoke an API token");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var client = ClientFactory.Create();

            await client.RevokeTokenAsync(name, ct);
            Console.WriteLine($"Token '{name}' revoked.");
        });

        return command;
    }
}