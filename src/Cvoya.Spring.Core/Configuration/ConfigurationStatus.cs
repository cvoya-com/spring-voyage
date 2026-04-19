// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

/// <summary>
/// Terminal state of an <see cref="IConfigurationRequirement"/> evaluation. The
/// startup validator combines this with <see cref="SeverityLevel"/> and the
/// requirement's <see cref="IConfigurationRequirement.IsMandatory"/> flag to
/// decide whether the host may continue booting.
/// </summary>
/// <remarks>
/// Added late, always. New states tack onto the end so downstream consumers
/// that switch on the enum keep compiling when the platform grows a state
/// without an exhaustive switch.
/// </remarks>
public enum ConfigurationStatus
{
    /// <summary>
    /// Requirement is satisfied. The subsystem has everything it needs and
    /// the validator is happy. Pair with <see cref="SeverityLevel.Warning"/>
    /// to express "met but degraded" — e.g. an ephemeral dev key that
    /// works today but will be lost on restart.
    /// </summary>
    Met = 0,

    /// <summary>
    /// Optional requirement was not configured. Only valid when
    /// <see cref="IConfigurationRequirement.IsMandatory"/> is <c>false</c>.
    /// The subsystem keeps the host booting but features that depend on it
    /// register themselves as disabled. The report surfaces the reason and
    /// a <see cref="ConfigurationRequirementStatus.Suggestion"/> operators
    /// can act on.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// Requirement is configured, but the value is unusable (malformed
    /// connection string, PEM that doesn't parse, unreachable endpoint
    /// flagged as mandatory, etc.). When paired with
    /// <see cref="IConfigurationRequirement.IsMandatory"/> <c>true</c>, the
    /// validator aborts startup with the supplied
    /// <see cref="ConfigurationRequirementStatus.FatalError"/>. Optional
    /// subsystems with <see cref="Invalid"/> status boot the host but are
    /// flagged as broken in the report.
    /// </summary>
    Invalid = 2,
}