// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tiny <see cref="IServiceScopeFactory"/> stand-in that hands launcher
/// tests a per-call DI scope returning a pre-canned
/// <see cref="ILlmCredentialResolver"/>. Lets the launcher tests drive the
/// scope-then-resolve flow the production launcher uses without spinning
/// up a real <see cref="ServiceProvider"/>.
/// </summary>
internal static class TestScopeFactory
{
    public static IServiceScopeFactory For(ILlmCredentialResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}