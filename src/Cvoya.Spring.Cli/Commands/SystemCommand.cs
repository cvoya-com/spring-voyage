// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Builds the <c>spring system</c> command tree (#616). Exposes the startup
/// configuration report produced by the platform's validator:
/// <list type="bullet">
///   <item><c>spring system configuration</c> — prints every subsystem as a table.</item>
///   <item><c>spring system configuration --json</c> — prints the raw JSON.</item>
///   <item><c>spring system configuration &lt;subsystem&gt;</c> — drills into one subsystem.</item>
/// </list>
/// Reads <c>GET /api/v1/platform/system/configuration</c>, which is anonymous in the OSS
/// build and returns a cached snapshot taken at host startup — see
/// <c>docs/architecture/configuration.md</c>.
/// </summary>
public static class SystemCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Creates the <c>system</c> command with its configuration sub-verbs.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var systemCommand = new Command("system", "Inspect platform-deploy configuration.");
        systemCommand.Subcommands.Add(CreateConfigurationCommand(outputOption));
        return systemCommand;
    }

    private static Command CreateConfigurationCommand(Option<string> outputOption)
    {
        var command = new Command(
            "configuration",
            "Print the cached startup configuration report (all subsystems, or drill into one).");

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print the raw JSON payload returned by the server (equivalent to --output json on other verbs).",
        };
        var subsystemArg = new Argument<string?>("subsystem")
        {
            Description = "Optional subsystem name (e.g. \"Database\", \"GitHub Connector\") to drill into.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        command.Options.Add(jsonOption);
        command.Arguments.Add(subsystemArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var asJson = parseResult.GetValue(jsonOption) || output == "json";
            var subsystem = parseResult.GetValue(subsystemArg);

            var baseUrl = ResolveBaseUrl();
            var report = await FetchReportAsync(baseUrl, ct).ConfigureAwait(false);

            if (report is null)
            {
                Console.Error.WriteLine(
                    "No configuration report returned. Is the API host running and is GET /api/v1/platform/system/configuration reachable?");
                Environment.Exit(1);
                return;
            }

            if (subsystem is not null)
            {
                var match = report.Subsystems
                    .FirstOrDefault(s =>
                        string.Equals(s.SubsystemName, subsystem, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    Console.Error.WriteLine(
                        $"No subsystem named '{subsystem}' found. Known: {string.Join(", ", report.Subsystems.Select(s => s.SubsystemName))}.");
                    Environment.Exit(1);
                    return;
                }

                if (asJson)
                {
                    Console.WriteLine(JsonSerializer.Serialize(match, JsonOptions));
                }
                else
                {
                    Console.Write(RenderSubsystem(match));
                }
                return;
            }

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            }
            else
            {
                Console.Write(RenderReport(report));
            }
        });

        return command;
    }

    private static string ResolveBaseUrl()
    {
        var config = CliConfig.Load();
        return Environment.GetEnvironmentVariable("SPRING_API_URL")
            ?? config.Endpoint
            ?? "http://localhost:5000";
    }

    private static async Task<ConfigurationReportDto?> FetchReportAsync(
        string baseUrl, CancellationToken ct)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        using var response = await http.GetAsync(
            "api/v1/platform/system/configuration", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(
                $"GET /api/v1/platform/system/configuration failed: {(int)response.StatusCode} {response.StatusCode}.");
            Environment.Exit(1);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<ConfigurationReportDto>(
            stream, JsonOptions, ct).ConfigureAwait(false);
    }

    private static string RenderReport(ConfigurationReportDto report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Overall: {report.Status}   (generated {report.GeneratedAt:O})");
        sb.AppendLine();

        foreach (var subsystem in report.Subsystems)
        {
            sb.Append(RenderSubsystem(subsystem));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RenderSubsystem(SubsystemDto subsystem)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{subsystem.Status}] {subsystem.SubsystemName}");
        foreach (var req in subsystem.Requirements)
        {
            var mandatory = req.IsMandatory ? "mandatory" : "optional";
            sb.AppendLine($"  - {req.DisplayName} ({req.RequirementId}) [{req.Status} / {req.Severity}] — {mandatory}");
            if (!string.IsNullOrWhiteSpace(req.Reason))
            {
                sb.AppendLine($"      reason: {req.Reason}");
            }
            if (!string.IsNullOrWhiteSpace(req.Suggestion))
            {
                sb.AppendLine($"      suggestion: {req.Suggestion}");
            }
            if (req.EnvironmentVariableNames is { Count: > 0 })
            {
                sb.AppendLine($"      env: {string.Join(", ", req.EnvironmentVariableNames)}");
            }
            if (!string.IsNullOrWhiteSpace(req.ConfigurationSectionPath))
            {
                sb.AppendLine($"      section: {req.ConfigurationSectionPath}");
            }
            if (!string.IsNullOrWhiteSpace(req.DocumentationUrl))
            {
                sb.AppendLine($"      docs: {req.DocumentationUrl}");
            }
        }
        return sb.ToString();
    }

    // Wire shape. We deliberately deserialise with our own minimal DTOs rather
    // than depending on Kiota regen — the framework's ConfigurationReport
    // record is the wire contract, and a local mirror keeps `spring system
    // configuration` working even when the OpenAPI regen hasn't run.
    private sealed record ConfigurationReportDto(
        string Status,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<SubsystemDto> Subsystems);

    private sealed record SubsystemDto(
        string SubsystemName,
        string Status,
        IReadOnlyList<RequirementDto> Requirements);

    private sealed record RequirementDto(
        string RequirementId,
        string DisplayName,
        string Description,
        bool IsMandatory,
        string Status,
        string Severity,
        string? Reason,
        string? Suggestion,
        IReadOnlyList<string> EnvironmentVariableNames,
        string? ConfigurationSectionPath,
        string? DocumentationUrl);
}