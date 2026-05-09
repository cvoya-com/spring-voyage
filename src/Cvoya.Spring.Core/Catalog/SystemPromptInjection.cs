// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

using System.Text.Json.Serialization;

/// <summary>
/// How the assembled system prompt reaches an <see cref="AgentRuntime"/>'s
/// container. Per ADR-0038 decision 2's "universal cross-runtime fields".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SystemPromptInjectionKind>))]
public enum SystemPromptInjectionKind
{
    /// <summary>
    /// Written to a workspace-relative file the runtime reads on start
    /// (e.g. <c>AGENTS.md</c>, <c>GEMINI.md</c>).
    /// </summary>
    File = 0,

    /// <summary>Delivered through an environment variable.</summary>
    EnvVar = 1,

    /// <summary>Passed on the command line (e.g. <c>--system-prompt</c>).</summary>
    Argv = 2,
}

/// <summary>
/// How a runtime receives the assembled system prompt.
/// </summary>
/// <param name="Kind">Delivery mechanism.</param>
/// <param name="FilePath">
/// Workspace-relative path where the dispatcher writes the prompt
/// (required when <see cref="Kind"/> is <see cref="SystemPromptInjectionKind.File"/>).
/// </param>
/// <param name="EnvVarName">
/// Env var carrying the prompt (required when <see cref="Kind"/> is
/// <see cref="SystemPromptInjectionKind.EnvVar"/>).
/// </param>
/// <param name="ArgName">
/// CLI flag carrying the prompt (required when <see cref="Kind"/> is
/// <see cref="SystemPromptInjectionKind.Argv"/>).
/// </param>
public sealed record SystemPromptInjection(
    SystemPromptInjectionKind Kind,
    string? FilePath = null,
    string? EnvVarName = null,
    string? ArgName = null);
