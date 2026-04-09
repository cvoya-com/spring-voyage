/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
