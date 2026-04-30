// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.ErrorHandling;

using Microsoft.Kiota.Abstractions;

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
            DefaultValueFactory = _ => "table",
            // #1067 — bind --output recursively so the flag is recognised on
            // every subcommand regardless of placement (e.g. both
            // `spring --output json unit create demo` and
            // `spring unit create demo --output json` parse cleanly). System
            // .CommandLine 2.0.6 supports this on the option itself, which
            // sidesteps the parse-error hint hack we used to recommend.
            Recursive = true,
        };
        outputOption.AcceptOnlyFromAmong("table", "json");

        // #1071 — recursive --verbose lets every subcommand opt into a
        // stack-trace dump on failure. We honour SPRING_CLI_DEBUG=1 in the
        // environment as an equivalent (handled inside ApiExceptionRenderer)
        // so debugging stays one env-var away in CI logs.
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Print stack traces and additional diagnostics on failure",
            Recursive = true,
        };

        var rootCommand = new RootCommand("Spring Voyage CLI")
        {
            Options = { outputOption, verboseOption },
            Subcommands =
            {
                AuthCommand.Create(outputOption),
                AgentCommand.Create(outputOption),
                UnitCommand.Create(outputOption),
                MessageCommand.Create(outputOption),
                ThreadCommand.Create(outputOption),
                EngagementCommand.Create(outputOption),
                InboxCommand.Create(outputOption),
                ActivityCommand.Create(outputOption),
                AgentRuntimeCommand.Create(outputOption),
                ConnectorCommand.Create(outputOption),
                AnalyticsCommand.Create(outputOption),
                CostCommand.Create(outputOption),
                DirectoryCommand.Create(outputOption),
                PackageCommand.Create(outputOption),
                PlatformCommand.Create(outputOption),
                SecretCommand.Create(outputOption),
                SystemCommand.Create(outputOption),
                TemplateCommand.Create(outputOption),
                GitHubAppCommand.Create(outputOption),
                ApplyCommand.Create()
            }
        };

        var parseResult = rootCommand.Parse(args);

        // #1071 — central catch for Kiota ApiException so unmapped status
        // codes (404/403/500/...) no longer surface as raw .NET stack
        // traces. Each command's per-call try/catch can still produce
        // command-specific messages where it adds value, but anything that
        // escapes the action runs through the swappable
        // IApiExceptionRenderer here.
        try
        {
            return await parseResult.InvokeAsync(null, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            var output = SafeGetValue(parseResult, outputOption) ?? "table";
            var verbose = SafeGetValue(parseResult, verboseOption);
            return ApiExceptionRenderer.Instance.Render(
                ex,
                new CliRenderContext(output, verbose));
        }
    }

    // ParseResult.GetValue throws if the option isn't bound (parse failed
    // before InvokeAsync got a chance to bind it). Treat that as "fall
    // back to defaults" so the renderer never throws inside the catch
    // block — which would replace one cryptic message with another.
    private static T? SafeGetValue<T>(ParseResult parseResult, Option<T> option)
    {
        try
        {
            return parseResult.GetValue(option);
        }
        catch
        {
            return default;
        }
    }
}