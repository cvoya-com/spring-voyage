// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Defines how a clone relates to its parent agent for lifecycle management.
/// </summary>
public enum AttachmentMode
{
    /// <summary>
    /// The clone operates independently. Its lifecycle is not tied to the parent.
    /// </summary>
    Detached,

    /// <summary>
    /// The clone is attached to the parent. When the parent is destroyed, the clone is also destroyed.
    /// </summary>
    Attached
}