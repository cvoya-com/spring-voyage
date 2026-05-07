// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

using System.Text.Json.Serialization;

/// <summary>
/// How an <see cref="AgentRuntime"/> receives the platform's thread / session
/// id. Per ADR-0038 decision 2's "universal cross-runtime fields".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThreadBindingKind>))]
public enum ThreadBindingKind
{
    /// <summary>Thread id is delivered as a CLI argument (e.g. <c>--resume &lt;id&gt;</c>).</summary>
    CliArg = 0,

    /// <summary>Thread id is delivered via an environment variable.</summary>
    EnvVar = 1,

    /// <summary>The runtime has no session concept. Reserved for completeness.</summary>
    None = 2,
}

/// <summary>
/// How the platform delivers the runtime-session id to an
/// <see cref="AgentRuntime"/>'s container.
/// </summary>
/// <param name="Kind">Delivery mechanism.</param>
/// <param name="ArgName">
/// CLI flag carrying the id (required when <see cref="Kind"/> is
/// <see cref="ThreadBindingKind.CliArg"/>).
/// </param>
/// <param name="EnvVarName">
/// Env var holding the id (required when <see cref="Kind"/> is
/// <see cref="ThreadBindingKind.EnvVar"/>).
/// </param>
public sealed record ThreadBinding(
    ThreadBindingKind Kind,
    string? ArgName = null,
    string? EnvVarName = null);