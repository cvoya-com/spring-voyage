// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

using System;

/// <summary>
/// Explicit, audited escape hatch for operations that legitimately need
/// to read or write across tenants — for example database migrations,
/// system-wide analytics jobs, and administrative tooling.
/// </summary>
/// <remarks>
/// <para>
/// <strong>When to use this.</strong> The EF Core query filter on every
/// tenant-scoped entity restricts each query to the current tenant. A
/// handful of call sites must legitimately see rows from every tenant:
/// the <c>DatabaseMigrator</c> (which runs before any tenant context
/// exists), system-wide analytics rollups, and platform administrative
/// operations. Those — and only those — call
/// <see cref="BeginBypass(string)"/> and perform their work inside the
/// returned scope.
/// </para>
/// <para>
/// <strong>When NOT to use this.</strong> Normal business queries, unit
/// orchestration, message routing, and anything driven by a user request
/// must never call <see cref="BeginBypass(string)"/>. If a feature
/// appears to need cross-tenant reads to serve a user, the correct
/// answer is almost always to rethink the feature rather than reach for
/// the bypass. Never call <c>IgnoreQueryFilters()</c> directly from
/// business code.
/// </para>
/// <para>
/// <strong>Nesting.</strong> The scope is nesting-safe: a bypass that
/// opens inside another active bypass keeps the outer scope alive until
/// both have disposed. Implementations are expected to track this via a
/// depth counter rather than a boolean flag, and <see cref="IsBypassActive"/>
/// returns <see langword="true"/> whenever the counter is non-zero.
/// </para>
/// <para>
/// <strong>Audit.</strong> Every <see cref="BeginBypass(string)"/> call
/// emits a structured log entry at Information level with the caller's
/// <c>reason</c>, the enter timestamp, and — on dispose — the scope's
/// duration. These entries are an audit signal; do not silence them.
/// </para>
/// <para>
/// <strong>Extensibility.</strong> The OSS default in
/// <c>Cvoya.Spring.Dapr.Tenancy.TenantScopeBypass</c> is registered via
/// <c>TryAddSingleton</c>. The private cloud can replace it with a
/// permission-checked variant (for example, one that refuses to open a
/// scope unless the caller holds a platform-admin grant) without
/// touching any call site.
/// </para>
/// </remarks>
public interface ITenantScopeBypass
{
    /// <summary>
    /// Gets a value indicating whether a bypass scope is currently
    /// active on the calling asynchronous flow. EF Core query filters
    /// for tenant-scoped entities consult this flag and, when it is
    /// <see langword="true"/>, allow rows from every tenant through.
    /// The soft-delete filter is untouched.
    /// </summary>
    bool IsBypassActive { get; }

    /// <summary>
    /// Opens a new cross-tenant bypass scope. The scope remains active
    /// until the returned <see cref="IDisposable"/> is disposed, at
    /// which point <see cref="IsBypassActive"/> reverts to the value it
    /// held before the call (supporting nested scopes).
    /// </summary>
    /// <param name="reason">
    /// Short human-readable description of why the scope is being
    /// opened. Surfaced in the audit log entry; must not be
    /// <see langword="null"/> or whitespace.
    /// </param>
    /// <returns>
    /// A disposable that closes the bypass scope and logs the scope's
    /// duration.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> is <see langword="null"/>, empty, or
    /// whitespace.
    /// </exception>
    IDisposable BeginBypass(string reason);
}