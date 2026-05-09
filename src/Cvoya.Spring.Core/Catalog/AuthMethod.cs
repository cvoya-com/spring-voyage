// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

using System.Text.Json.Serialization;

/// <summary>
/// Auth methods a <see cref="ModelProvider"/> may accept and an
/// <see cref="AgentRuntime"/> per-edge may consume. Closed enum per
/// ADR-0038 decision 4. "No auth" is represented by absence — empty
/// <see cref="ModelProvider.AuthMethods"/> on the provider and an absent
/// <see cref="AgentRuntimeProviderEdge.AuthMethod"/> on the per-edge entry.
/// There is intentionally no <c>None</c> token.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthMethod>))]
public enum AuthMethod
{
    /// <summary>OAuth-issued token (e.g. <c>claude setup-token</c>).</summary>
    Oauth = 0,

    /// <summary>Long-lived API key (e.g. <c>sk-ant-…</c>, <c>sk-…</c>).</summary>
    ApiKey = 1,
}
