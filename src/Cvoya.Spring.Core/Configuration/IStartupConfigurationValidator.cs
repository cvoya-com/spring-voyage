// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

/// <summary>
/// Read-only seam over the cached <see cref="ConfigurationReport"/> produced at
/// host startup. Implementations are expected to populate the cache before any
/// consumer (HTTP endpoint, CLI command, portal page) resolves it — the
/// default implementation registers itself as the first hosted service.
/// </summary>
/// <remarks>
/// Extracted as an interface so downstream hosts (the private cloud repo,
/// multi-tenant deployments) can swap in a tenant-scoped implementation that
/// filters the report by the caller's principal. Endpoints depend on this
/// interface, not on the concrete validator.
/// </remarks>
public interface IStartupConfigurationValidator
{
    /// <summary>
    /// The cached report. Returns <see cref="ConfigurationReport.Empty"/> when
    /// the validator has not yet run (extremely rare — the validator is the
    /// first hosted service).
    /// </summary>
    ConfigurationReport Report { get; }
}