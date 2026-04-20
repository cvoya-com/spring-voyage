// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// The kind of credential an <see cref="IAgentRuntime"/> expects at accept
/// time. Used by the wizard to render the correct input control and by the
/// credential-health store to categorize stored secrets.
/// </summary>
public enum AgentRuntimeCredentialKind
{
    /// <summary>
    /// The runtime does not require a credential (e.g. a local model
    /// server reachable without auth). The wizard credential step is
    /// skipped.
    /// </summary>
    None = 0,

    /// <summary>
    /// The runtime expects a long-lived API key (e.g.
    /// <c>sk-ant-...</c>, <c>sk-...</c>). Rendered as a masked text input.
    /// </summary>
    ApiKey = 1,

    /// <summary>
    /// The runtime expects an OAuth access token (e.g. from a CLI
    /// <c>login</c> flow). Rendered as a masked text input; the wizard may
    /// show instructions for obtaining the token.
    /// </summary>
    OAuthToken = 2,
}