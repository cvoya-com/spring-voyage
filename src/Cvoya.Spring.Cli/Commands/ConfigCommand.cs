// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.CommandLine;

using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the "config" command tree for the CLI's local connection settings
/// stored in <c>~/.spring/config.json</c> — the API endpoint and, when present,
/// a stored auth token. These verbs are local-only: they never call the API.
/// </summary>
public static class ConfigCommand
{
    /// <summary>
    /// Creates the "config" command with <c>set</c> and <c>show</c> subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var configCommand = new Command(
            "config",
            "Manage the CLI's local connection settings (~/.spring/config.json).");

        // Bare `spring config` mirrors `spring config show` rather than dumping help.
        configCommand.SetAction((ParseResult parseResult) =>
            RenderShow(parseResult.GetValue(outputOption) ?? "table"));

        configCommand.Subcommands.Add(CreateSetCommand());
        configCommand.Subcommands.Add(CreateShowCommand(outputOption));

        return configCommand;
    }

    private static Command CreateSetCommand()
    {
        // `set` is a grouping verb; `endpoint` is the only field today. Keeping it
        // a subcommand (rather than `config set <key> <value>`) makes the one
        // settable field self-documenting and leaves room for future fields.
        var setCommand = new Command("set", "Set a CLI connection setting.");
        setCommand.Subcommands.Add(CreateSetEndpointCommand());
        return setCommand;
    }

    private static Command CreateSetEndpointCommand()
    {
        var urlArg = new Argument<string>("url")
        {
            Description = "Absolute API endpoint URL, e.g. http://localhost:8080",
        };
        var command = new Command(
            "endpoint",
            "Point the CLI at an API endpoint. Preserves a stored auth token.");
        command.Arguments.Add(urlArg);

        command.SetAction((ParseResult parseResult) =>
        {
            var url = parseResult.GetValue(urlArg)!;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Console.Error.WriteLine(
                    $"Invalid endpoint '{url}'. Expected an absolute http(s) URL, e.g. http://localhost:8080.");
                Environment.Exit(1);
                return;
            }

            // Load → mutate → save round-trips the whole object, so an existing
            // ApiToken (or any future field) survives the endpoint update. This is
            // the merge that lets a re-install refresh a changed Caddy host port
            // without clobbering the operator's token (#3091) — no bash JSON surgery.
            var config = CliConfig.Load();
            config.Endpoint = url;
            config.Save();

            Console.WriteLine($"Endpoint set to {url}");
        });

        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var command = new Command(
            "show",
            "Show the CLI's connection settings and the endpoint it will use.");
        command.SetAction((ParseResult parseResult) =>
            RenderShow(parseResult.GetValue(outputOption) ?? "table"));
        return command;
    }

    private static void RenderShow(string output)
    {
        var config = CliConfig.Load();
        var envOverride = Environment.GetEnvironmentVariable("SPRING_API_URL");
        var hasOverride = !string.IsNullOrEmpty(envOverride);

        // Mirror ClientFactory's resolution order: SPRING_API_URL wins over the
        // config-file endpoint (a per-invocation --endpoint flag wins over both,
        // but that's not a persisted setting so it isn't shown here).
        var effective = hasOverride ? envOverride! : config.Endpoint;
        var source = hasOverride ? "SPRING_API_URL" : "config.json";
        var tokenConfigured = !string.IsNullOrEmpty(config.ApiToken);

        if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
        {
            // Never emit the token value itself — only whether one is set.
            Console.WriteLine(OutputFormatter.FormatJsonPlain(new
            {
                configPath = CliConfig.DefaultConfigFilePath,
                endpoint = config.Endpoint,
                springApiUrl = hasOverride ? envOverride : null,
                effectiveEndpoint = effective,
                effectiveSource = source,
                tokenConfigured,
            }));
            return;
        }

        Console.WriteLine($"configPath          {CliConfig.DefaultConfigFilePath}");
        Console.WriteLine($"endpoint            {config.Endpoint}");
        Console.WriteLine($"SPRING_API_URL      {(hasOverride ? envOverride : "(not set)")}");
        Console.WriteLine($"effectiveEndpoint   {effective}  (from {source})");
        Console.WriteLine($"tokenConfigured     {(tokenConfigured ? "yes" : "no")}");
    }
}
