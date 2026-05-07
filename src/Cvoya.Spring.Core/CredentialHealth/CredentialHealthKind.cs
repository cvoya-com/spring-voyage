// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.CredentialHealth;

/// <summary>
/// Discriminator for the kind of subject a <c>credential_health</c> row
/// is tracking. Agent runtimes and connectors share the same store so
/// wizard banners, portal read-only views, and the
/// <c>spring … credentials status</c> CLI verb can enumerate a unified
/// health picture via a single read.
/// </summary>
public enum CredentialHealthKind
{
    /// <summary>
    /// The subject is a model provider declared in the runtime catalogue
    /// (ADR-0038) — the row's <c>subject_id</c> is the provider's id
    /// (e.g. <c>anthropic</c>, <c>openai</c>). The value name is kept as
    /// <c>AgentRuntime</c> for v0.1 wire stability while the persisted
    /// rows migrate; the conceptual subject is the provider.
    /// </summary>
    AgentRuntime = 0,

    /// <summary>
    /// The subject is an <see cref="Connectors.IConnectorType"/> — the
    /// row's <c>subject_id</c> is the connector's <c>Slug</c>.
    /// </summary>
    Connector = 1,
}