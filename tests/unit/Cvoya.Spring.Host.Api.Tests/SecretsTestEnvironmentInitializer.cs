// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Sets <c>SPRING_SECRETS_AES_KEY</c>, the dispatcher env vars, and the
/// orchestration callback base URL for the duration of every test in this
/// assembly.
/// </summary>
/// <remarks>
/// <para>
/// <c>SPRING_SECRETS_AES_KEY</c> is required because the platform no longer
/// ships an in-memory ephemeral dev key — every <c>SecretsEncryptor</c>
/// instance constructed during host startup needs a real base64 32-byte key.
/// </para>
/// <para>
/// The <c>Dispatcher__*</c> env vars satisfy the now-mandatory
/// <c>DispatcherConfigurationRequirement</c> (#2518). The API host runs
/// <c>PersistentAgentRegistry</c> as a hosted service and the validator
/// aborts startup if the dispatcher endpoint is unset. The values are
/// never actually called from the test process: every test that exercises
/// dispatcher-dependent code paths swaps the relevant services for mocks
/// via <c>CustomWebApplicationFactory</c>. The URL just has to be a
/// syntactically valid absolute http(s) URI so the validator does not trip
/// the malformed-URL branch.
/// </para>
/// <para>
/// <c>CallbackBaseUrl__BaseUrl</c> satisfies the now-mandatory
/// <c>CallbackBaseUrlConfigurationRequirement</c> (#2597). The API
/// host stamps this value onto every runtime container as
/// <c>SPRING_CALLBACK_URL</c>; the validator aborts startup if it is unset
/// or malformed. As with the dispatcher endpoint the value is never dialled
/// from the test process — it just has to be a syntactically valid absolute
/// http(s) URI.
/// </para>
/// <para>
/// We set them once at module load (before any test factory builds a host)
/// rather than on every <c>UseSetting</c> call site, which keeps the bare
/// <c>new WebApplicationFactory&lt;Program&gt;()</c> usages across the
/// suite from each having to learn the dispatcher / orchestration-callback
/// contract. Existing values are preserved so an operator can override them
/// at the shell.
/// </para>
/// </remarks>
internal static class SecretsTestEnvironmentInitializer
{
    /// <summary>Deterministic AES-256 base64 key for tests.</summary>
    public const string TestAesKeyBase64 = "8w7eyN4Jf1g3AX9BPkej9gV2hWV1LO6lWIvs6RRQcAw=";

    [ModuleInitializer]
    public static void Initialize()
    {
        // Only set when not already configured — keeps an operator's
        // ambient SPRING_SECRETS_AES_KEY (e.g. when running tests against
        // a key they care about) from being silently overridden.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPRING_SECRETS_AES_KEY")))
        {
            Environment.SetEnvironmentVariable("SPRING_SECRETS_AES_KEY", TestAesKeyBase64);
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Dispatcher__BaseUrl")))
        {
            Environment.SetEnvironmentVariable("Dispatcher__BaseUrl", "http://spring-dispatcher.test/");
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Dispatcher__BearerToken")))
        {
            Environment.SetEnvironmentVariable("Dispatcher__BearerToken", "test-token");
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CallbackBaseUrl__BaseUrl")))
        {
            Environment.SetEnvironmentVariable("CallbackBaseUrl__BaseUrl", "http://spring-caddy:8443/");
        }
    }
}
