// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

/// <summary>
/// Configuration options bound from the <c>Tenancy</c> configuration
/// section. Controls the lifecycle of the
/// <see cref="DefaultTenantBootstrapService"/> and any future tenancy-
/// scoped infrastructure that does not belong under <c>Secrets</c>
/// (which carries the per-request tenant id used by
/// <see cref="ConfiguredTenantContext"/> and the unit-scoped secrets
/// HTTP API).
/// </summary>
public class TenancyOptions
{
    /// <summary>
    /// The configuration section name used for binding.
    /// </summary>
    public const string SectionName = "Tenancy";

    /// <summary>
    /// Whether the host should run the
    /// <see cref="DefaultTenantBootstrapService"/> on startup. Defaults
    /// to <c>true</c> so a fresh OSS deployment comes up with the
    /// canonical <c>"default"</c> tenant present and every registered
    /// <see cref="Cvoya.Spring.Core.Tenancy.ITenantSeedProvider"/>
    /// applied. Operators driving tenant provisioning out-of-band
    /// (the private cloud host, scripted onboarding, integration test
    /// harnesses that pre-seed) set this to <c>false</c> so the
    /// hosted service is a strict no-op.
    /// </summary>
    public bool BootstrapDefaultTenant { get; set; } = true;
}