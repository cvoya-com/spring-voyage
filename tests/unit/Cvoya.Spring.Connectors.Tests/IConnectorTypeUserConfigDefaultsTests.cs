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
/// Default-interface-method coverage for the user-config schema seam
/// (ADR-0047 §4 / issue #2495). A connector that only implements the
/// historically-required members of <see cref="IConnectorType"/> inherits
/// no-op defaults for both <see cref="IConnectorType.UserConfigType"/> and
/// <see cref="IConnectorType.GetUserConfigSchemaAsync"/> — that is the path
/// Arxiv and WebSearch rely on to stay unchanged in this phase.
/// </summary>
public class IConnectorTypeUserConfigDefaultsTests
{
    [Fact]
    public void UserConfigType_DefaultsToNull()
    {
        IConnectorType connector = new NoOpUserConfigConnectorType();

        connector.UserConfigType.ShouldBeNull();
    }

    [Fact]
    public async Task GetUserConfigSchemaAsync_DefaultsToNull()
    {
        IConnectorType connector = new NoOpUserConfigConnectorType();

        var schema = await connector.GetUserConfigSchemaAsync(TestContext.Current.CancellationToken);

        schema.ShouldBeNull();
    }

    // Minimal fixture that implements only the required members of
    // IConnectorType. Inheriting the user-config defaults is the entire
    // point of the test — overriding either of them here would defeat
    // the no-op-inheritance assertion.
    private sealed class NoOpUserConfigConnectorType : IConnectorType
    {
        public Guid TypeId { get; } = new("00000000-0000-0000-0000-000000000001");
        public string Slug => "test-noop";
        public string DisplayName => "Test no-op";
        public string Description => "Fixture connector exercising the IConnectorType default-interface members.";
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

    private sealed record NoOpConfig();
}
