// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ITenantScopeBypass"/> implementation for the OSS
/// core. Tracks bypass nesting depth in an <see cref="AsyncLocal{T}"/>
/// counter so the flag flows across <c>async/await</c> boundaries, and
/// emits a structured audit log entry on scope open and close.
/// </summary>
/// <remarks>
/// <para>
/// Registered via <c>TryAddSingleton</c> so the private cloud repo can
/// pre-register a permission-checked variant (for example, one that
/// requires a platform-admin grant on the caller principal before
/// allowing <see cref="BeginBypass"/> to return a live scope).
/// </para>
/// <para>
/// A singleton is safe because all mutable state is held in the
/// <see cref="AsyncLocal{T}"/>, which partitions per logical
/// asynchronous flow. No field on the instance is written after
/// construction.
/// </para>
/// </remarks>
public sealed partial class TenantScopeBypass : ITenantScopeBypass
{
    // AsyncLocal<int> nesting-depth counter — flows across async/await,
    // partitions per logical flow, and supports nested scopes cleanly.
    // Reading an AsyncLocal that was never written returns default(int) = 0.
    private static readonly AsyncLocal<int> s_depth = new();

    private readonly ILogger<TenantScopeBypass> _logger;

    /// <summary>
    /// Creates a new <see cref="TenantScopeBypass"/>.
    /// </summary>
    /// <param name="logger">Logger for audit entries on scope open / close.</param>
    public TenantScopeBypass(ILogger<TenantScopeBypass> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsBypassActive => s_depth.Value > 0;

    /// <inheritdoc />
    public IDisposable BeginBypass(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A non-empty reason is required when opening a tenant-scope bypass.",
                nameof(reason));
        }

        s_depth.Value++;
        var depth = s_depth.Value;
        var caller = CaptureCallerContext();
        var openedAt = DateTimeOffset.UtcNow;

        LogBypassOpened(_logger, reason, caller, depth, openedAt);

        return new BypassScope(_logger, reason, caller, openedAt);
    }

    /// <summary>
    /// EF Core query filters for tenant-scoped entities consult this
    /// flag. When <see langword="true"/> the tenant predicate short-circuits
    /// to <c>true</c> and rows from every tenant are returned. The soft-delete
    /// filter is untouched. Integration of this flag with the EF query
    /// filter ships with the sibling PR that adds the per-entity
    /// <c>TenantId</c> column (see the <c>#675</c> issue); until that
    /// lands the flag is read-only and the filter has nothing to bypass
    /// yet.
    /// </summary>
    /// <remarks>
    /// Example integration once query filters exist:
    /// <code>
    /// // modelBuilder.Entity&lt;Foo&gt;()
    /// //     .HasQueryFilter(e =&gt;
    /// //         _tenantScopeBypass.IsBypassActive
    /// //         || e.TenantId == _tenantContext.CurrentTenantId);
    /// </code>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsActive() => s_depth.Value > 0;

    private static string CaptureCallerContext()
    {
        // Walk the stack past this helper and past BeginBypass to find
        // the first frame outside TenantScopeBypass. Stack traces in
        // Release builds can be thin — be defensive and fall back to
        // "unknown" rather than failing the scope.
        try
        {
            var trace = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var frame = trace.GetFrame(i);
                var method = frame?.GetMethod();
                if (method is null)
                {
                    continue;
                }

                var declaring = method.DeclaringType;
                if (declaring is null)
                {
                    continue;
                }

                if (declaring == typeof(TenantScopeBypass) ||
                    declaring == typeof(BypassScope))
                {
                    continue;
                }

                var typeName = declaring.FullName ?? declaring.Name;
                return $"{typeName}.{method.Name}";
            }
        }
        catch
        {
            // Diagnostic best-effort; never fail the scope on a stack
            // walk issue.
        }

        return "unknown";
    }

    [LoggerMessage(
        EventId = 2500,
        Level = LogLevel.Information,
        Message = "Tenant-scope bypass opened: reason='{Reason}' caller='{Caller}' depth={Depth} at {OpenedAt:O}")]
    private static partial void LogBypassOpened(
        ILogger logger, string reason, string caller, int depth, DateTimeOffset openedAt);

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Information,
        Message = "Tenant-scope bypass closed: reason='{Reason}' caller='{Caller}' duration={DurationMs:F1}ms")]
    private static partial void LogBypassClosed(
        ILogger logger, string reason, string caller, double durationMs);

    private sealed class BypassScope : IDisposable
    {
        private readonly ILogger<TenantScopeBypass> _logger;
        private readonly string _reason;
        private readonly string _caller;
        private readonly DateTimeOffset _openedAt;
        private int _disposed;

        public BypassScope(
            ILogger<TenantScopeBypass> logger,
            string reason,
            string caller,
            DateTimeOffset openedAt)
        {
            _logger = logger;
            _reason = reason;
            _caller = caller;
            _openedAt = openedAt;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                // Double-dispose is a no-op. In particular, do not
                // decrement the counter twice — that would corrupt an
                // outer scope sharing the same async flow.
                return;
            }

            // Guard against a negative counter (would happen only if
            // Dispose ran on a different async flow than Begin). Clamp
            // at zero rather than throwing — we're cleaning up on a
            // finally path and must not mask the caller's exception.
            if (s_depth.Value > 0)
            {
                s_depth.Value--;
            }

            var duration = (DateTimeOffset.UtcNow - _openedAt).TotalMilliseconds;
            LogBypassClosed(_logger, _reason, _caller, duration);
        }
    }
}