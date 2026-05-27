// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging.Rendering;

/// <summary>
/// Default <see cref="IMessagePayloadRendererRegistry"/>. Walks every
/// registered <see cref="IMessagePayloadRenderer"/> filtered by
/// <see cref="IMessagePayloadRenderer.TargetType"/> (a <c>null</c>
/// target matches any <see cref="MessageType"/>), orders the survivors
/// by descending <see cref="IMessagePayloadRenderer.Priority"/>, and
/// returns the first non-null <see cref="IMessagePayloadRenderer.Render"/>.
/// </summary>
/// <remarks>
/// The renderer set is read at construction so the registry can sort
/// once; renderers are themselves singletons in the host DI container.
/// Equal-priority ties resolve by the order
/// <see cref="System.Linq.Enumerable.OrderByDescending{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>
/// presents them — a stable sort under .NET — so registration order is
/// the tie-breaker.
/// </remarks>
public sealed class MessagePayloadRendererRegistry : IMessagePayloadRendererRegistry
{
    private readonly IReadOnlyList<IMessagePayloadRenderer> _renderers;

    /// <summary>
    /// Creates a new registry over <paramref name="renderers"/>. The
    /// renderers are sorted once by descending priority; per-call
    /// selection only filters by <see cref="IMessagePayloadRenderer.TargetType"/>
    /// and asks each remaining renderer's <see cref="IMessagePayloadRenderer.CanRender"/>.
    /// </summary>
    public MessagePayloadRendererRegistry(IEnumerable<IMessagePayloadRenderer> renderers)
    {
        ArgumentNullException.ThrowIfNull(renderers);

        _renderers = renderers
            .OrderByDescending(r => r.Priority)
            .ToArray();
    }

    /// <inheritdoc />
    public string? TryRender(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        foreach (var renderer in _renderers)
        {
            if (renderer.TargetType is { } target && target != message.Type)
            {
                continue;
            }

            if (!renderer.CanRender(message))
            {
                continue;
            }

            var rendered = renderer.Render(message);
            if (rendered is not null)
            {
                return rendered;
            }
        }

        return null;
    }
}
