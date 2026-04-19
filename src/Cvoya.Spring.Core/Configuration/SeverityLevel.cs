// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

/// <summary>
/// Severity that a requirement's status carries — independent of the
/// <see cref="ConfigurationStatus"/> itself. Lets the validator express
/// "met but degraded" (<see cref="ConfigurationStatus.Met"/> +
/// <see cref="Warning"/>) without collapsing into a single enum with too
/// many branches.
/// </summary>
/// <remarks>
/// Severity is an advisory signal for operators — the validator's
/// abort-on-boot rule is driven by <see cref="ConfigurationStatus"/> +
/// <see cref="IConfigurationRequirement.IsMandatory"/>, not by severity.
/// Portal and CLI renderers use severity to pick a badge colour and a
/// headline tone.
/// </remarks>
public enum SeverityLevel
{
    /// <summary>
    /// Informational — everything nominal. Default for
    /// <see cref="ConfigurationStatus.Met"/> with no caveats.
    /// </summary>
    Information = 0,

    /// <summary>
    /// Degraded state — the subsystem works today but the operator should
    /// be aware of a caveat (ephemeral secrets key, default dev endpoint,
    /// optional feature deliberately disabled, etc.).
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Broken — the subsystem is misconfigured or unreachable. When paired
    /// with <see cref="IConfigurationRequirement.IsMandatory"/> <c>true</c>,
    /// the validator fails fast; otherwise the report surfaces the error
    /// and the host keeps booting with the dependent feature disabled.
    /// </summary>
    Error = 2,
}