// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring template</c> verb family (#395 / PR-PLAT-PKG-1).
/// The <c>show</c> verb is the only one in this PR; it prints the raw
/// YAML manifest for a unit template so an operator can pipe it to
/// <c>spring apply -f -</c> or inspect the fields before instantiating.
/// A future <c>spring template list</c> would duplicate
/// <c>spring package show &lt;name&gt;</c>'s template section, so we
/// intentionally ship only <c>show</c> today.
/// </summary>
public static class TemplateCommand
{
    /// <summary>
    /// Creates the <c>template</c> command root.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command("template", "Inspect package templates (unit manifests)");
        command.Subcommands.Add(CreateShowCommand(outputOption));
        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var refArgument = new Argument<string>("ref")
        {
            Description = "Template reference in '<package>/<template>' form (e.g. 'software-engineering/engineering-team').",
        };

        var command = new Command(
            "show",
            "Print the raw YAML manifest for a unit template. Use this to preview what 'spring apply' would instantiate.");
        command.Arguments.Add(refArgument);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var reference = parseResult.GetValue(refArgument)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var (package, name, error) = ParseReference(reference);
            if (error is not null)
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var detail = await client.GetUnitTemplateAsync(package!, name!, ct);
            if (detail is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Template '{package}/{name}' not found. Run 'spring package show {package}' to see its templates.");
                Environment.Exit(1);
                return;
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(detail));
                return;
            }

            // Table view emits a copy-friendly header + raw YAML body.
            // The portal surfaces the same two-row view on the template
            // detail page, so CLI and portal stay at parity (§ 5.1 of
            // docs/design/portal-exploration.md).
            Console.WriteLine($"Package:  {detail.Package}");
            Console.WriteLine($"Template: {detail.Name}");
            Console.WriteLine($"Path:     {detail.Path}");
            Console.WriteLine();
            Console.WriteLine(detail.Yaml);
        });

        return command;
    }

    /// <summary>
    /// Parses a <c>package/template</c> reference. Exposed so the
    /// parser tests can exercise the same validation the command uses
    /// without spinning up a mocked HTTP handler just to exercise the
    /// "bad input" paths.
    /// </summary>
    public static (string? Package, string? Name, string? Error) ParseReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, null, "Template reference is required. Example: 'software-engineering/engineering-team'.");
        }

        var slash = reference.IndexOf('/');
        if (slash <= 0 || slash == reference.Length - 1)
        {
            return (null, null,
                "Template reference must be in '<package>/<template>' form. " +
                "Example: 'software-engineering/engineering-team'.");
        }

        // Reject nested separators so the CLI can't construct a request
        // that would traverse sub-directories under a package's units/
        // tree. The server also guards against this but the CLI error is
        // clearer than a 404 from a crafted path.
        if (reference.IndexOf('/', slash + 1) >= 0)
        {
            return (null, null,
                "Template reference must contain exactly one '/' separator between package and template.");
        }

        var package = reference[..slash];
        var name = reference[(slash + 1)..];
        return (package, name, null);
    }
}