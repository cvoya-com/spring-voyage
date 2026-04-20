// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Describes the credential an <see cref="IAgentRuntime"/> expects at accept
/// time. Carries the credential <see cref="Kind"/> and an optional
/// <see cref="DisplayHint"/> the wizard can surface above the input control
/// (e.g. "starts with <c>sk-ant-</c>" or a link to a login guide).
/// </summary>
/// <param name="Kind">The kind of credential the runtime accepts.</param>
/// <param name="DisplayHint">Optional human-facing hint describing expected format or how to obtain the credential. May be <c>null</c>.</param>
public sealed record AgentRuntimeCredentialSchema(
    AgentRuntimeCredentialKind Kind,
    string? DisplayHint = null);