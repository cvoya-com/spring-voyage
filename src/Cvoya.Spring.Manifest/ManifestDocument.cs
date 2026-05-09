// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using YamlDotNet.Serialization;

/// <summary>
/// Root YAML document shape for a unit manifest.
/// Only the <c>unit</c> key is recognised today.
/// </summary>
public class ManifestDocument
{
    /// <summary>The unit definition.</summary>
    [YamlMember(Alias = "unit")]
    public UnitManifest? Unit { get; set; }
}
