// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Default <see cref="IReflectionActionHandlerRegistry"/> — a thin
/// dictionary-backed lookup over the registered handlers. Not sealed so the
/// private cloud repo can extend it (e.g. to gate handlers behind feature
/// flags) without reimplementing the whole interface.
/// </summary>
/// <remarks>
/// Duplicate <see cref="IReflectionActionHandler.ActionType"/> values are
/// resolved "first wins" so the cloud host can override an OSS default by
/// registering its replacement ahead of the default registrations in its DI
/// module.
/// </remarks>
public class ReflectionActionHandlerRegistry : IReflectionActionHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IReflectionActionHandler> _handlers;

    /// <summary>
    /// Initializes a new <see cref="ReflectionActionHandlerRegistry"/> from
    /// the DI-provided collection of handlers.
    /// </summary>
    /// <param name="handlers">The registered handlers.</param>
    public ReflectionActionHandlerRegistry(IEnumerable<IReflectionActionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, IReflectionActionHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.ActionType))
            {
                continue;
            }

            // First-wins semantics — see remarks.
            map.TryAdd(handler.ActionType, handler);
        }

        _handlers = map;
    }

    /// <inheritdoc />
    public virtual IReflectionActionHandler? Find(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return null;
        }

        return _handlers.TryGetValue(actionType, out var handler) ? handler : null;
    }
}