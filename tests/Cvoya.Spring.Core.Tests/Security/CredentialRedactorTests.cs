// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Security;

using System;

using Cvoya.Spring.Core.Security;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="CredentialRedactor"/>. The redactor is the single
/// hook probe output flows through before it reaches an activity event or the
/// <c>LastValidationErrorJson</c> column, so these cases are load-bearing for
/// the "no raw credential ever gets persisted" invariant.
/// </summary>
public class CredentialRedactorTests
{
    [Fact]
    public void Redact_CanaryCredentialInStderrSample_IsReplaced()
    {
        var credential = "SPRING_PROBE_CANARY_" + Guid.NewGuid().ToString("N");
        var stderr = $"curl: (22) The requested URL returned error: 401\nheader Authorization: Bearer {credential}\nprobe: auth rejected";

        var redacted = CredentialRedactor.Redact(stderr, credential);

        redacted.ShouldNotContain(credential);
        redacted.ShouldContain("***");
        redacted.ShouldContain("auth rejected");
    }

    [Fact]
    public void Redact_CredentialAppearsThreeTimes_AllOccurrencesReplaced()
    {
        var credential = "sk-abc123";
        var text = $"use {credential} once, {credential} twice, and {credential} thrice";

        var redacted = CredentialRedactor.Redact(text, credential);

        redacted.ShouldNotContain(credential);
        redacted.Split("***").Length.ShouldBe(4);
    }

    [Fact]
    public void Redact_EmptyText_ReturnsEmpty()
    {
        var redacted = CredentialRedactor.Redact(string.Empty, "some-secret");

        redacted.ShouldBe(string.Empty);
    }

    [Fact]
    public void Redact_EmptyCredential_ReturnsInputUnchanged()
    {
        const string text = "stderr: nothing interesting happened";

        var redacted = CredentialRedactor.Redact(text, string.Empty);

        redacted.ShouldBe(text);
    }

    [Fact]
    public void Redact_NullCredential_ReturnsInputUnchanged()
    {
        const string text = "stderr: still nothing interesting";

        var redacted = CredentialRedactor.Redact(text, null!);

        redacted.ShouldBe(text);
    }

    [Fact]
    public void Redact_CredentialEmbeddedMidWord_IsStillReplaced()
    {
        const string credential = "XYZ";
        const string text = "prefixXYZsuffix";

        var redacted = CredentialRedactor.Redact(text, credential);

        redacted.ShouldBe("prefix***suffix");
    }

    [Fact]
    public void Redact_NonAsciiSurroundingContext_LeftIntactOutsideReplacements()
    {
        const string credential = "token-42";
        const string text = "你好 token-42 世界 — ✨ edge-case";

        var redacted = CredentialRedactor.Redact(text, credential);

        redacted.ShouldBe("你好 *** 世界 — ✨ edge-case");
    }

    [Fact]
    public void Redact_NullText_Throws()
    {
        Should.Throw<ArgumentNullException>(() => CredentialRedactor.Redact(null!, "secret"));
    }

    [Fact]
    public void Redact_CaseSensitive_DoesNotMatchDifferentCase()
    {
        const string credential = "Secret123";
        const string text = "my value is secret123 in lowercase";

        var redacted = CredentialRedactor.Redact(text, credential);

        redacted.ShouldBe(text);
    }
}