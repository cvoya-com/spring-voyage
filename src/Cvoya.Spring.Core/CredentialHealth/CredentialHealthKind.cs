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
    /// The subject is an <see cref="AgentRuntimes.IAgentRuntime"/> — the
    /// row's <c>subject_id</c> is the runtime's <c>Id</c>.
    /// </summary>
    AgentRuntime = 0,

    /// <summary>
    /// The subject is an <see cref="Connectors.IConnectorType"/> — the
    /// row's <c>subject_id</c> is the connector's <c>Slug</c>.
    /// </summary>
    Connector = 1,
}