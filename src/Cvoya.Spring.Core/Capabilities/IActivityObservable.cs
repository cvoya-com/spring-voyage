// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a component that emits observable activity events.
/// </summary>
public interface IActivityObservable
{
    /// <summary>
    /// Gets the observable stream of activity events from this component.
    /// </summary>
    IObservable<ActivityEvent> ActivityStream { get; }
}
