// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Provisioning;

using System.Collections.Generic;

/// <summary>
/// Maps a <see cref="SlackProvisionedCredentials"/> bundle onto the
/// ordered set of (secret-name, value) pairs to persist, using the
/// canonical <see cref="SlackSecretNames"/>. Shared by the CLI's
/// <c>SlackCredentialWriter</c> (platform / tenant secret writes) and the
/// server-side install endpoint so both surfaces write exactly the same
/// secrets in the same order (#2882). Fields Slack omitted are reported in
/// <see cref="SecretMapping.MissingFields"/> rather than written as empty
/// values — callers surface them as non-fatal warnings.
/// </summary>
public static class SlackCredentialSecretMapper
{
    /// <summary>A single secret to persist.</summary>
    /// <param name="Name">The canonical secret name (see <see cref="SlackSecretNames"/>).</param>
    /// <param name="Value">The credential value.</param>
    public sealed record SecretPair(string Name, string Value);

    /// <summary>
    /// The result of mapping a credential bundle: the pairs to write, plus
    /// the labels of any fields Slack omitted.
    /// </summary>
    /// <param name="Pairs">The (name, value) pairs to persist, in canonical order.</param>
    /// <param name="MissingFields">Human-readable labels of fields Slack did not return.</param>
    public sealed record SecretMapping(
        IReadOnlyList<SecretPair> Pairs,
        IReadOnlyList<string> MissingFields);

    /// <summary>
    /// Builds the secret pairs to persist for the supplied credentials.
    /// Only non-empty fields are emitted as pairs; empty / null fields are
    /// recorded in <see cref="SecretMapping.MissingFields"/>.
    /// </summary>
    public static SecretMapping BuildSecretPairs(SlackProvisionedCredentials credentials)
    {
        System.ArgumentNullException.ThrowIfNull(credentials);

        var pairs = new List<SecretPair>();
        var missing = new List<string>();

        void Add(string name, string? value, string fieldLabel)
        {
            if (!string.IsNullOrEmpty(value))
            {
                pairs.Add(new SecretPair(name, value));
            }
            else
            {
                missing.Add(fieldLabel);
            }
        }

        Add(SlackSecretNames.AppId, credentials.AppId, "AppId");
        Add(SlackSecretNames.ClientId, credentials.ClientId, "ClientId");
        Add(SlackSecretNames.ClientSecret, credentials.ClientSecret, "ClientSecret");
        Add(SlackSecretNames.SigningSecret, credentials.SigningSecret, "SigningSecret");
        Add(SlackSecretNames.VerificationToken, credentials.VerificationToken, "VerificationToken");
        Add(SlackSecretNames.RedirectUri, credentials.RedirectUri, "RedirectUri");

        return new SecretMapping(pairs, missing);
    }
}
