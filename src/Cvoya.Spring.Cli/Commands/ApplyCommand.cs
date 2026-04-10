// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

/// <summary>
/// Builds the "apply" command for declarative resource creation from YAML files.
/// Phase 1 stub: parses and prints what would be created.
/// </summary>
public static class ApplyCommand
{
    /// <summary>
    /// Creates the "apply" command.
    /// </summary>
    public static Command Create()
    {
        var fileOption = new Option<string>("-f", "--file") { Description = "Path to the YAML manifest file", Required = true };
        var command = new Command("apply", "Apply a resource manifest");
        command.Options.Add(fileOption);

        command.SetAction((ParseResult parseResult) =>
        {
            var filePath = parseResult.GetValue(fileOption)!;

            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"Error: File '{filePath}' not found.");
                return 1;
            }

            var content = File.ReadAllText(filePath);
            Console.WriteLine($"[dry-run] Would apply manifest from: {filePath}");
            Console.WriteLine($"[dry-run] File size: {content.Length} bytes");
            Console.WriteLine("[dry-run] Apply is a stub in Phase 1. No resources were created.");
            return 0;
        });

        return command;
    }
}
