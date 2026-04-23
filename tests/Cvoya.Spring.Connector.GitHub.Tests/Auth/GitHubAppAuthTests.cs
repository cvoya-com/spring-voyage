// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Auth;

using System.Security.Cryptography;
using System.Text;

using Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Regression coverage for #1130 — <see cref="GitHubAppAuth"/> is registered
/// as a singleton and its <see cref="GitHubAppAuth.GenerateJwt"/> method must
/// keep working across repeated calls. The previous implementation disposed
/// the underlying <c>RSA</c> at the end of every call via a <c>using</c>; the
/// signature provider that <c>Microsoft.IdentityModel.Tokens</c> caches in
/// <c>CryptoProviderFactory.Default</c> retained a reference to that disposed
/// RSA, so the second and subsequent JWT generations threw
/// <c>ObjectDisposedException</c> ("RSAOpenSsl"). Existing test coverage
/// stopped at "the first call works" — these tests close that gap.
/// </summary>
public class GitHubAppAuthTests
{
    private static GitHubAppAuth CreateAuth()
    {
        // Generate a fresh RSA key per call. Microsoft.IdentityModel.Tokens
        // caches SignatureProvider instances inside CryptoProviderFactory.Default
        // keyed on the key material — using a unique key per test ensures these
        // tests do not interact through that process-wide cache. (In production
        // GitHubAppAuth is a singleton, so there is only ever one instance with
        // one key for the lifetime of the cache, and this is a non-issue.)
        using var rsa = RSA.Create(2048);
        var options = new GitHubConnectorOptions
        {
            AppId = 123456,
            PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(),
        };

        return new GitHubAppAuth(options, NullLoggerFactory.Instance);
    }

    [Fact]
    public void GenerateJwt_RepeatedCalls_AllSucceed()
    {
        // Regression for #1130: the original implementation disposed the RSA
        // after the first call, so call #2 threw ObjectDisposedException.
        using var auth = CreateAuth();

        var jwt1 = auth.GenerateJwt();
        var jwt2 = auth.GenerateJwt();
        var jwt3 = auth.GenerateJwt();

        AssertLooksLikeRs256Jwt(jwt1);
        AssertLooksLikeRs256Jwt(jwt2);
        AssertLooksLikeRs256Jwt(jwt3);
    }

    [Fact]
    public async Task GenerateJwt_ConcurrentCalls_AllSucceed()
    {
        using var auth = CreateAuth();

        const int taskCount = 16;
        const int callsPerTask = 4;

        var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
        {
            var jwts = new string[callsPerTask];
            for (var i = 0; i < callsPerTask; i++)
            {
                jwts[i] = auth.GenerateJwt();
            }
            return jwts;
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        var flattened = results.SelectMany(r => r).ToArray();
        flattened.Length.ShouldBe(taskCount * callsPerTask);
        foreach (var jwt in flattened)
        {
            AssertLooksLikeRs256Jwt(jwt);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var auth = CreateAuth();

        // Force the lazy RSA to be created so Dispose() actually has work
        // to do — we want to exercise the "real" disposal path, not the
        // "never-initialized" short-circuit.
        _ = auth.GenerateJwt();

        auth.ShouldBeAssignableTo<IDisposable>();

        Should.NotThrow(() => auth.Dispose());
        Should.NotThrow(() => auth.Dispose());
    }

    private static void AssertLooksLikeRs256Jwt(string jwt)
    {
        jwt.ShouldNotBeNullOrEmpty();

        var segments = jwt.Split('.');
        segments.Length.ShouldBe(3, $"JWT should have three dot-separated segments, got: {jwt}");

        var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(segments[0]));
        headerJson.ShouldContain("\"alg\"");
        headerJson.ShouldContain("RS256");

        // Sanity-check the other two segments decode without error.
        Should.NotThrow(() => Base64UrlDecode(segments[1]));
        Should.NotThrow(() => Base64UrlDecode(segments[2]));
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}