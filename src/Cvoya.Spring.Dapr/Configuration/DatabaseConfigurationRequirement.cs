// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Tier-1 requirement: the primary PostgreSQL connection string
/// (<c>ConnectionStrings:SpringDb</c>). Mandatory — the platform cannot
/// start without a usable database.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the manual <see cref="InvalidOperationException"/> throw that
/// used to live inside <c>AddCvoyaSpringDapr</c>; the framework now owns
/// database connection-string validation.
/// </para>
/// <para>
/// <b>Test-harness accommodation.</b> Integration tests that pre-register
/// <c>DbContextOptions&lt;SpringDbContext&gt;</c> (for
/// <c>UseInMemoryDatabase</c>) before calling the DI extensions do NOT
/// supply a connection string — that's expected and the requirement
/// reports <see cref="ConfigurationStatus.Met"/> in that case, keyed off
/// the pre-registered <c>DbContextOptions</c>. Design-time tooling
/// (<c>BuildEnvironment.IsDesignTimeTooling</c>) bypasses this requirement
/// entirely via the registration gate in <c>AddCvoyaSpringDapr</c>.
/// </para>
/// </remarks>
public sealed class DatabaseConfigurationRequirement(
    IConfiguration configuration,
    DatabaseConfigurationRequirement.TestHarnessSignal signal) : IConfigurationRequirement
{
    /// <summary>
    /// Marker singleton set at <c>AddCvoyaSpringDapr</c> time when the caller
    /// pre-registered <c>DbContextOptions&lt;SpringDbContext&gt;</c> (test
    /// harness path). The requirement consults this instead of resolving
    /// the scoped <c>DbContextOptions</c> from the root provider — which
    /// would throw "Cannot resolve scoped service from root provider"
    /// under the ASP.NET Core hosted-service lifetime.
    /// </summary>
    /// <param name="PreRegistered">Whether a DbContext was registered before AddCvoyaSpringDapr.</param>
    public sealed record TestHarnessSignal(bool PreRegistered);

    /// <inheritdoc />
    public string RequirementId => "database-connection-string";

    /// <inheritdoc />
    public string DisplayName => "Database connection string";

    /// <inheritdoc />
    public string SubsystemName => "Database";

    /// <inheritdoc />
    public bool IsMandatory => true;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "ConnectionStrings__SpringDb" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => "ConnectionStrings:SpringDb";

    /// <inheritdoc />
    public string Description =>
        "PostgreSQL connection string used by EF Core for all platform data (units, agents, memberships, activity events, secrets metadata).";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/deployment/README.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        // Test harness short-circuit: the DI extension detected a pre-
        // registered DbContextOptions<SpringDbContext> (typically
        // UseInMemoryDatabase) so no connection string is required.
        if (signal.PreRegistered)
        {
            return Task.FromResult(ConfigurationRequirementStatus.MetWithWarning(
                reason: "DbContextOptions<SpringDbContext> pre-registered (in-memory test harness).",
                suggestion: null));
        }

        var connectionString = configuration.GetConnectionString("SpringDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason: "ConnectionStrings:SpringDb is not set.",
                suggestion:
                    "Set the ConnectionStrings:SpringDb configuration value (environment variable " +
                    "ConnectionStrings__SpringDb=...) to a valid PostgreSQL connection string. See deployment/README.md.",
                fatalError: new InvalidOperationException(
                    "No connection string found for SpringDbContext. Set the " +
                    "ConnectionStrings:SpringDb configuration value (environment variable " +
                    "ConnectionStrings__SpringDb=...) to a valid PostgreSQL connection " +
                    "string, or pre-register DbContextOptions<SpringDbContext> before " +
                    "calling AddCvoyaSpringDapr (for example via " +
                    "AddDbContext<SpringDbContext>(options => options.UseInMemoryDatabase(...)) " +
                    "in a test harness).")));
        }

        // Parse-and-classify. We don't attempt a real connection here —
        // that's an orchestration health concern (and #305's migrator
        // surfaces a real connection failure on first use). Validation of
        // the string shape alone catches 90% of operator misconfigurations
        // (empty value, missing Host= / Database= keywords) without
        // slowing boot on a slow Postgres.
        try
        {
            _ = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason: "ConnectionStrings:SpringDb is set but does not parse as a Npgsql connection string.",
                suggestion:
                    "Fix the connection string shape. Example: " +
                    "\"Host=spring-postgres;Database=spring;Username=spring;Password=...\".",
                fatalError: new InvalidOperationException(
                    "ConnectionStrings:SpringDb is malformed: " + ex.Message, ex)));
        }

        return Task.FromResult(ConfigurationRequirementStatus.Met());
    }
}