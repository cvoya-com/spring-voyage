// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Runtime;

/// <summary>
/// Mints signed per-invocation callback tokens from the shared
/// <see cref="CallbackToken"/> claim shape.
/// </summary>
public interface ICallbackTokenIssuer
{
    /// <summary>
    /// Issues a signed compact-JWT string carrying the supplied callback claims.
    /// </summary>
    /// <param name="claims">Claim shape for the token.</param>
    /// <returns>The signed compact-JWT string.</returns>
    string Issue(CallbackToken claims);
}