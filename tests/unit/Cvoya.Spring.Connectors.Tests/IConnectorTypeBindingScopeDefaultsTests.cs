// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors.Tests;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;

using Microsoft.AspNetCore.Routing;

using Shouldly;

using Xunit;

/// <summary>
/// Default-interface-method coverage for the
/// <see cref="IConnectorType.BindingScope"/> seam (ADR-0061 §1). A
/// connector that does not override the property inherits
/// <see cref="BindingScope.Unit"/> — preserves the historical per-unit
/// shape for every connector landed before ADR-0061.
/// </summary>
public class IConnectorTypeBindingScopeDefaultsTests
{
    [Fact]
    public void BindingScope_DefaultsToUnit()
    {
        IConnectorType connector = new NoOpBindingScopeConnectorType();

        connector.BindingScope.ShouldBe(BindingScope.Unit);
    }

    [Fact]
    public void BindingScope_TenantOverride_IsRespected()
    {
        IConnectorType connector = new TenantScopedConnectorType();

        connector.BindingScope.ShouldBe(BindingScope.Tenant);
    }

    // Minimal fixture that implements only the required members of
    // IConnectorType. Inheriting the BindingScope default is the entire
    // point of the test.
    private sealed class NoOpBindingScopeConnectorType : IConnectorType
    {
        public Guid TypeId { get; } = new("00000000-0000-0000-0000-000000000010");
        public string Slug => "test-noop-binding";
        public string DisplayName => "Test no-op binding scope";
        public string Description => "Fixture connector inheriting BindingScope.Unit.";
        public Type ConfigType => typeof(NoOpConfig);

        public void MapRoutes(IEndpointRouteBuilder group)
        {
            // intentional no-op
        }

        public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);

        public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TenantScopedConnectorType : IConnectorType
    {
        public Guid TypeId { get; } = new("00000000-0000-0000-0000-000000000011");
        public string Slug => "test-tenant-scope";
        public string DisplayName => "Test tenant-scoped";
        public string Description => "Fixture connector returning BindingScope.Tenant.";
        public Type ConfigType => typeof(NoOpConfig);

        public BindingScope BindingScope => BindingScope.Tenant;

        public void MapRoutes(IEndpointRouteBuilder group)
        {
            // intentional no-op
        }

        public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);

        public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record NoOpConfig();
}
