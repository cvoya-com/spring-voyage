/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core;

/// <summary>
/// Base exception class for all Spring Voyage platform exceptions.
/// </summary>
public class SpringException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpringException"/> class.
    /// </summary>
    public SpringException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpringException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SpringException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpringException"/> class with a specified error message
    /// and a reference to the inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SpringException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
