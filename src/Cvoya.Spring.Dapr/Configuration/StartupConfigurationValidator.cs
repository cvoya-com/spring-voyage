// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IStartupConfigurationValidator"/> implementation. Runs
/// every registered <see cref="IConfigurationRequirement"/> once at host
/// startup, aggregates the results into a <see cref="ConfigurationReport"/>,
/// caches the report for the lifetime of the host, and aborts startup when a
/// mandatory requirement reports <see cref="ConfigurationStatus.Invalid"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering.</b> Registered via <c>AddCvoyaSpringConfigurationValidator</c>
/// so it takes the first <c>IHostedService</c> slot. Anything that depends on
/// a validated config (EF connections, connectors, the Ollama probe, etc.)
/// must be registered after this service.
/// </para>
/// <para>
/// <b>Fail-fast policy.</b> A mandatory requirement with
/// <see cref="ConfigurationStatus.Invalid"/> throws the requirement's
/// <see cref="ConfigurationRequirementStatus.FatalError"/> from
/// <c>StartAsync</c>. When multiple mandatory requirements are invalid we
/// aggregate them into one <see cref="AggregateException"/> so operators see
/// every problem in a single boot attempt.
/// </para>
/// <para>
/// <b>Caching.</b> The report is cached — there is no revalidation. The
/// <c>POST /system/configuration/revalidate</c> idea is deliberately
/// out-of-scope for PR 1; it can be added as a follow-up if operators want
/// it. See <c>docs/architecture/configuration.md</c>.
/// </para>
/// </remarks>
public class StartupConfigurationValidator : IHostedService, IStartupConfigurationValidator
{
    private readonly IReadOnlyList<IConfigurationRequirement> _requirements;
    private readonly ILogger<StartupConfigurationValidator> _logger;
    private ConfigurationReport _report = ConfigurationReport.Empty;

    /// <summary>
    /// Creates a new validator bound to the supplied requirement set.
    /// </summary>
    public StartupConfigurationValidator(
        IEnumerable<IConfigurationRequirement> requirements,
        ILogger<StartupConfigurationValidator> logger)
    {
        _requirements = requirements?.ToArray() ?? throw new ArgumentNullException(nameof(requirements));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ConfigurationReport Report => _report;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Startup configuration validation: evaluating {Count} requirement(s).",
            _requirements.Count);

        var rows = new List<(IConfigurationRequirement Requirement, ConfigurationRequirementStatus Status)>(_requirements.Count);

        foreach (var requirement in _requirements)
        {
            ConfigurationRequirementStatus status;
            try
            {
                status = await requirement.ValidateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A validator that throws is treated as Invalid — the validator
                // contract expects validators to catch their own exceptions and
                // return ConfigurationRequirementStatus.Invalid(...). Surface a
                // targeted message so the operator knows which requirement
                // blew up.
                _logger.LogError(ex,
                    "Configuration requirement '{RequirementId}' threw during validation.",
                    requirement.RequirementId);

                status = ConfigurationRequirementStatus.Invalid(
                    reason: $"Validator threw: {ex.Message}",
                    suggestion: "See the host logs for the full stack trace and fix the underlying error.",
                    fatalError: ex);
            }

            rows.Add((requirement, status));
        }

        _report = BuildReport(rows);

        // Log at a consistent level per row so operators can grep the boot log.
        foreach (var (requirement, status) in rows)
        {
            var level = status.Severity switch
            {
                SeverityLevel.Error => LogLevel.Error,
                SeverityLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            };
            _logger.Log(level,
                "Configuration requirement [{Subsystem} / {RequirementId}] {Status} ({Severity}). {Reason}",
                requirement.SubsystemName,
                requirement.RequirementId,
                status.Status,
                status.Severity,
                status.Reason ?? "(no detail)");
        }

        // Fail fast when a mandatory requirement is invalid. Collect every
        // failure so operators see all problems in a single boot attempt
        // rather than fixing one, rebooting, and tripping the next.
        var fatal = rows
            .Where(r => r.Requirement.IsMandatory && r.Status.Status == ConfigurationStatus.Invalid)
            .Select(r => r.Status.FatalError
                ?? new InvalidOperationException(
                    $"Mandatory configuration requirement '{r.Requirement.RequirementId}' ({r.Requirement.SubsystemName}) is invalid: "
                    + (r.Status.Reason ?? "no details supplied.")))
            .ToList();

        if (fatal.Count == 1)
        {
            throw fatal[0];
        }

        if (fatal.Count > 1)
        {
            throw new AggregateException(
                $"Startup configuration validation failed: {fatal.Count} mandatory requirement(s) are invalid. See inner exceptions.",
                fatal);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ConfigurationReport BuildReport(
        IReadOnlyList<(IConfigurationRequirement Requirement, ConfigurationRequirementStatus Status)> rows)
    {
        var subsystems = rows
            .GroupBy(r => r.Requirement.SubsystemName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var requirementRows = g
                    .Select(x => ToRow(x.Requirement, x.Status))
                    .ToList();
                return new SubsystemConfigurationReport(
                    g.Key,
                    Aggregate(g.Select(x => x.Status)),
                    requirementRows);
            })
            .ToList();

        var overall = Aggregate(rows.Select(r => r.Status));
        return new ConfigurationReport(overall, DateTimeOffset.UtcNow, subsystems);
    }

    private static RequirementStatus ToRow(
        IConfigurationRequirement requirement,
        ConfigurationRequirementStatus status) =>
        new(
            RequirementId: requirement.RequirementId,
            DisplayName: requirement.DisplayName,
            Description: requirement.Description,
            IsMandatory: requirement.IsMandatory,
            Status: status.Status,
            Severity: status.Severity,
            Reason: status.Reason,
            Suggestion: status.Suggestion,
            EnvironmentVariableNames: requirement.EnvironmentVariableNames ?? Array.Empty<string>(),
            ConfigurationSectionPath: requirement.ConfigurationSectionPath,
            DocumentationUrl: requirement.DocumentationUrl?.ToString());

    private static ConfigurationReportStatus Aggregate(IEnumerable<ConfigurationRequirementStatus> statuses)
    {
        var any = false;
        var hasInvalid = false;
        var hasDegraded = false;

        foreach (var s in statuses)
        {
            any = true;
            if (s.Status == ConfigurationStatus.Invalid)
            {
                hasInvalid = true;
            }
            else if (s.Status == ConfigurationStatus.Disabled || s.Severity == SeverityLevel.Warning)
            {
                hasDegraded = true;
            }
        }

        if (!any) return ConfigurationReportStatus.Healthy;
        if (hasInvalid) return ConfigurationReportStatus.Failed;
        if (hasDegraded) return ConfigurationReportStatus.Degraded;
        return ConfigurationReportStatus.Healthy;
    }
}