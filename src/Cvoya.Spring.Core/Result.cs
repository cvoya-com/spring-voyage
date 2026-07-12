// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Represents the result of an operation that can either succeed with a value or fail with an error.
/// </summary>
/// <typeparam name="TValue">The type of the success value.</typeparam>
/// <typeparam name="TError">The type of the error value.</typeparam>
public readonly record struct Result<TValue, TError>
{
    /// <summary>
    /// Gets the success value. Only valid when <see cref="IsSuccess"/> is <c>true</c>.
    /// </summary>
    public TValue? Value { get; }

    /// <summary>
    /// Gets the error value. Only valid when <see cref="IsSuccess"/> is <c>false</c>.
    /// </summary>
    public TError? Error { get; }

    /// <summary>
    /// Gets a value indicating whether the result represents a successful operation.
    /// </summary>
    public bool IsSuccess { get; }

    private Result(TValue? value, TError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    /// <summary>
    /// Creates a successful result with the specified value.
    /// </summary>
    /// <param name="value">The success value.</param>
    /// <returns>A successful result.</returns>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "These type-inferred factory methods are the public Result API; moving them would make expected-failure handling less clear at call sites.")]
    public static Result<TValue, TError> Success(TValue value) => new(value, default, true);

    /// <summary>
    /// Creates a failed result with the specified error.
    /// </summary>
    /// <param name="error">The error value.</param>
    /// <returns>A failed result.</returns>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "These type-inferred factory methods are the public Result API; moving them would make expected-failure handling less clear at call sites.")]
    public static Result<TValue, TError> Failure(TError error) => new(default, error, false);
}
