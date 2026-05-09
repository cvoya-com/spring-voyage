// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.ModelProviders;

using System.Text.Json.Serialization;

/// <summary>
/// The kind of credential a model provider expects at accept time. Used
/// by the wizard to render the correct input control and by the
/// credential-health store to categorize stored secrets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelProviderCredentialKind>))]
public enum ModelProviderCredentialKind
{
    /// <summary>
    /// The provider does not require a credential (e.g. a local model
    /// server reachable without auth). The wizard credential step is
    /// skipped.
    /// </summary>
    None = 0,

    /// <summary>
    /// The provider expects a long-lived API key (e.g.
    /// <c>sk-ant-...</c>, <c>sk-...</c>). Rendered as a masked text input.
    /// </summary>
    ApiKey = 1,

    /// <summary>
    /// The provider expects an OAuth access token (e.g. from a CLI
    /// <c>login</c> flow). Rendered as a masked text input; the wizard
    /// may show instructions for obtaining the token.
    /// </summary>
    OAuthToken = 2,
}
