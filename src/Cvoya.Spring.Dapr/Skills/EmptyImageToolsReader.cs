// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Default <see cref="IImageToolsReader"/> implementation registered by
/// <c>AddCvoyaSpringDapr()</c>. Returns an empty list for every subject
/// — the image-tier of <see cref="IToolGrantResolver"/> contributes
/// nothing until Sub C (#2336) ships the SDK introspection path and
/// registers a real reader.
/// </summary>
/// <remarks>
/// Wired via <c>TryAddSingleton</c> so Sub C's registration takes
/// precedence in the cloud overlay and once it lands in OSS. Keeping
/// the default empty (rather than throwing) lets the resolver's
/// image-tier walk be additive — present and tested today, populated
/// when Sub C arrives.
/// </remarks>
public sealed class EmptyImageToolsReader : IImageToolsReader
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ImageToolEntry>> GetImageToolsAsync(
        Address subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult<IReadOnlyList<ImageToolEntry>>(Array.Empty<ImageToolEntry>());
    }
}
