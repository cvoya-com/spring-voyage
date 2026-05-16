// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Collections.Generic;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Default in-memory <see cref="IToolRegistry"/> implementation. Thread-safe
/// for concurrent <see cref="Register"/> and read paths so an agent author
/// who fans tool registrations across initialisation tasks doesn't have to
/// hand-roll synchronisation.
/// </summary>
/// <remarks>
/// Backed by a <see cref="Dictionary{TKey, TValue}"/> guarded by a lock —
/// the surface is small (register + list + lookup) so a contended-write
/// pathology is unlikely. The list snapshot returned by <see cref="List"/>
/// is a defensive copy so callers can iterate without worrying about
/// concurrent <see cref="Register"/> invalidating the enumeration.
/// </remarks>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ToolDefinition> _definitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolHandler> _handlers =
        new(StringComparer.Ordinal);
    // Preserves insertion order — the dictionary doesn't, since
    // dictionary-iteration order is officially unspecified.
    private readonly List<string> _order = new();

    /// <inheritdoc />
    public void Register(ToolDefinition definition, ToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);

        // ToolDefinition's constructor already enforces ToolNaming.Pattern
        // (#2334 / Sub A); we re-check here so a hypothetical bypass (e.g. a
        // mocked record subtype) still fails at the registry boundary.
        if (!ToolNaming.IsValid(definition.Name))
        {
            throw new ArgumentException(
                $"Tool name '{definition.Name}' does not match the canonical pattern " +
                $"'{ToolNaming.Pattern}'. Tool ids must be lowercase, dotted-snake, " +
                "with a leading namespace segment (e.g. 'acme.echo').",
                nameof(definition));
        }

        lock (_lock)
        {
            if (_definitions.ContainsKey(definition.Name))
            {
                throw new InvalidOperationException(
                    $"A tool with name '{definition.Name}' is already registered. " +
                    "Each id may only be registered once per agent process.");
            }

            _definitions.Add(definition.Name, definition);
            _handlers.Add(definition.Name, handler);
            _order.Add(definition.Name);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> List()
    {
        lock (_lock)
        {
            // Return a fresh list — the registry's internal _order may grow
            // after this call and we don't want consumers iterating a live
            // collection.
            var snapshot = new List<ToolDefinition>(_order.Count);
            foreach (var name in _order)
            {
                snapshot.Add(_definitions[name]);
            }
            return snapshot;
        }
    }

    /// <inheritdoc />
    public ToolHandler? GetHandler(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        lock (_lock)
        {
            return _handlers.TryGetValue(name, out var handler) ? handler : null;
        }
    }
}
