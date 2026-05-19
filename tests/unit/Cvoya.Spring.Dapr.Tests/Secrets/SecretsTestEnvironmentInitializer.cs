// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Sets <c>SPRING_SECRETS_AES_KEY</c> for the duration of every test in
/// this assembly. Required because the platform no longer ships an
/// in-memory ephemeral dev key — every <c>SecretsEncryptor</c> instance
/// constructed during a test (directly or transitively via
/// <c>StartupConfigurationValidator</c> tests, DI wiring tests, etc.)
/// needs a real base64 32-byte key.
///
/// <para>
/// Tests inside <see cref="SecretsEnvironmentVariableCollection"/> still
/// save / restore the env var around their own assertions; this
/// initializer just establishes a sensible default value that's already
/// present when those tests run, instead of leaving the variable unset
/// and forcing every consumer to opt-in.
/// </para>
/// </summary>
internal static class SecretsTestEnvironmentInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Only set when not already configured — keeps an operator's
        // ambient SPRING_SECRETS_AES_KEY (e.g. when running tests against
        // a key they care about) from being silently overridden.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPRING_SECRETS_AES_KEY")))
        {
            Environment.SetEnvironmentVariable("SPRING_SECRETS_AES_KEY", SecretsTestKey.Base64);
        }
    }
}

/// <summary>
/// Sets default <c>Dispatcher__BaseUrl</c> / <c>Dispatcher__BearerToken</c>
/// values process-wide so tests that exercise the full DI graph
/// (<c>AddCvoyaSpringDapr</c> + <c>StartupConfigurationValidator</c>) do
/// not trip the now-mandatory <see cref="Cvoya.Spring.Dapr.Configuration.DispatcherConfigurationRequirement"/>
/// (#2518). Individual tests that specifically validate the missing-URL
/// branch construct their own requirement instance and pass an
/// <c>IOptions&lt;DispatcherClientOptions&gt;</c> with empty values, so
/// the process-wide default has no effect on them. Existing operator
/// env-var values are preserved.
/// </summary>
internal static class DispatcherTestEnvironmentInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Dispatcher__BaseUrl")))
        {
            Environment.SetEnvironmentVariable("Dispatcher__BaseUrl", "http://spring-dispatcher.test/");
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Dispatcher__BearerToken")))
        {
            Environment.SetEnvironmentVariable("Dispatcher__BearerToken", "test-token");
        }
    }
}
