// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

/// <summary>
/// A successfully retrieved cache entry. The <see cref="Value"/> is whatever
/// the caller cached (typed per call site) and <see cref="Age"/> is the
/// elapsed time since the entry was written — surfaced so diagnostics can log
/// how stale a hit was without a second clock read.
/// </summary>
/// <typeparam name="T">The value type stored under the key.</typeparam>
/// <param name="Value">The cached value.</param>
/// <param name="Age">Elapsed time since the entry was written.</param>
public readonly record struct CacheEntry<T>(T Value, TimeSpan Age);