// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

/// <summary>
/// Entry point for the Spring Voyage CLI.
/// </summary>
public class Program
{
    /// <summary>
    /// Builds the command tree and invokes the parsed command.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output format (table or json)",
            DefaultValueFactory = _ => "table"
        };
        outputOption.AcceptOnlyFromAmong("table", "json");

        var rootCommand = new RootCommand("Spring Voyage CLI")
        {
            Options = { outputOption },
            Subcommands =
            {
                AuthCommand.Create(outputOption),
                AgentCommand.Create(outputOption),
                UnitCommand.Create(outputOption),
                MessageCommand.Create(outputOption),
                ApplyCommand.Create()
            }
        };

        return await rootCommand.Parse(args).InvokeAsync(null, CancellationToken.None);
    }
}