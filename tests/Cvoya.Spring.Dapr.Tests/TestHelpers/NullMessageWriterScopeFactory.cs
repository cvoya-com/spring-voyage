// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tests-only <see cref="IServiceScopeFactory"/> that yields a scope whose
/// <see cref="IMessageWriter"/> resolves to a no-op. Used by pure unit
/// tests that construct <see cref="Routing.MessageRouter"/> directly and
/// don't exercise the EF persistence path; production composes the writer
/// through <c>AddCvoyaSpringDapr</c>'s scoped registration.
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
