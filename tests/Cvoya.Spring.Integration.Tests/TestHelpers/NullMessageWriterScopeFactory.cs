// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tests-only <see cref="IServiceScopeFactory"/> that yields a scope whose
/// <see cref="IMessageWriter"/> resolves to a no-op. Used by integration
/// test harnesses that build <see cref="Cvoya.Spring.Dapr.Routing.MessageRouter"/>
/// outside the host DI container and don't exercise the EF persistence path.
/// </summary>
internal static class NullMessageWriterScopeFactory
{
    public static IServiceScopeFactory Create()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMessageWriter, NullMessageWriter>();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class NullMessageWriter : IMessageWriter
    {
        public Task WriteAsync(Message message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
