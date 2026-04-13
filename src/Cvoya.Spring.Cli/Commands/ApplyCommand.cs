// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.CommandLine;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using Cvoya.Spring.Manifest;

/// <summary>
/// Builds the <c>apply</c> command, which parses a unit manifest YAML file and
/// drives the platform API via <see cref="SpringApiClient"/> to create the unit
/// and register its declared members.
/// </summary>
public static class ApplyCommand
{
    /// <summary>
    /// Creates the <c>apply</c> command.
    /// </summary>
    public static Command Create()
    {
        var fileOption = new Option<string>("-f", "--file")
        {
            Description = "Path to the YAML manifest file",
            Required = true,
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Parse the manifest and print the resolved plan without calling the API",
            DefaultValueFactory = _ => false,
        };
        var apiUrlOption = new Option<string?>("--api-url")
        {
            Description = "Override the Spring API base URL (falls back to $SPRING_API_URL, then the CLI config)",
        };

        var command = new Command("apply", "Apply a resource manifest");
        command.Options.Add(fileOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(apiUrlOption);

        command.SetAction(async (ParseResult parseResult, System.Threading.CancellationToken ct) =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var dryRun = parseResult.GetValue(dryRunOption);
            var apiUrlOverride = parseResult.GetValue(apiUrlOption);

            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"Error: File '{filePath}' not found.");
                return 1;
            }

            UnitManifest manifest;
            try
            {
                manifest = ApplyRunner.ParseFile(filePath);
            }
            catch (ManifestParseException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }

            if (dryRun)
            {
                ApplyRunner.PrintPlan(manifest, Console.Out);
                return 0;
            }

            var (httpClient, baseUrl) = CreateHttpClient(apiUrlOverride);
            using (httpClient)
            {
                var client = new SpringApiClient(httpClient, baseUrl);
                return await ApplyRunner.ApplyAsync(manifest, client, Console.Out, Console.Error, ct);
            }
        });

        return command;
    }

    /// <summary>
    /// Builds the HTTP client used for the real apply path.
    /// Resolution order for the base address: explicit <c>--api-url</c>, then
    /// the <c>SPRING_API_URL</c> environment variable, then the CLI config file.
    /// </summary>
    private static (HttpClient HttpClient, string BaseUrl) CreateHttpClient(string? apiUrlOverride)
    {
        var config = CliConfig.Load();
        var baseUrl = apiUrlOverride
            ?? Environment.GetEnvironmentVariable("SPRING_API_URL")
            ?? config.Endpoint;

        var httpClient = new HttpClient();

        if (config.ApiToken is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiToken);
        }

        return (httpClient, baseUrl);
    }
}