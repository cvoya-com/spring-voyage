// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Small parsing helpers shared by the built-in
/// <see cref="IReflectionActionHandler"/> implementations. Kept internal-to-
/// the-namespace rather than public API so the private cloud repo is not
/// coupled to the exact helper shape if we later refactor.
/// </summary>
internal static class ReflectionActionPayloadHelpers
{
    /// <summary>
    /// Reads <c>targetScheme</c> + <c>targetId</c> out of <paramref name="payload"/>
    /// and returns an <see cref="Address"/>, or <c>null</c> if either field is
    /// missing, blank, or fails to parse as a Guid.
    /// </summary>
    internal static Address? ReadTarget(JsonElement payload)
    {
        if (!TryGetString(payload, "targetScheme", out var scheme) ||
            string.IsNullOrWhiteSpace(scheme))
        {
            return null;
        }

        // Accept either "targetId" (canonical, post-#1629) or the legacy
        // "targetPath" key for in-flight payloads.
        if ((!TryGetString(payload, "targetId", out var rawId) ||
             string.IsNullOrWhiteSpace(rawId)) &&
            (!TryGetString(payload, "targetPath", out rawId) ||
             string.IsNullOrWhiteSpace(rawId)))
        {
            return null;
        }

        if (!Identifiers.GuidFormatter.TryParse(rawId, out var id))
        {
            return null;
        }

        return new Address(scheme, id);
    }

    /// <summary>
    /// Reads <c>threadId</c> out of <paramref name="payload"/>, or
    /// returns <c>null</c> when absent. Empty / whitespace values are
    /// normalised to <c>null</c> so callers never see a blank string.
    /// </summary>
    internal static string? ReadThreadId(JsonElement payload)
    {
        if (!TryGetString(payload, "threadId", out var id) ||
            string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return id;
    }

    /// <summary>
    /// <c>System.Text.Json</c> property reader with case-insensitive
    /// matching; returns <c>false</c> when the property is missing, is not
    /// a JSON string, or has a null value.
    /// </summary>
    internal static bool TryGetString(JsonElement payload, string propertyName, out string value)
    {
        value = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Case-insensitive lookup so agent-authored payloads that capitalise
        // differently still match.
        foreach (var property in payload.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return true;
            }

            return false;
        }

        return false;
    }
}