// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the <see cref="ArtefactValidationExitCodes"/> contract (T-08 / #950).
/// The table is additive-only public surface: these tests would fail the
/// moment anyone renumbers an existing code, which is the whole point —
/// operators script on these numbers and a silent renumber would break
/// every pipeline that branches on them.
/// </summary>
public class ArtefactValidationExitCodesTests
{
    [Theory]
    [InlineData(ArtefactValidationCodes.ImagePullFailed, 20)]
    [InlineData(ArtefactValidationCodes.ImageStartFailed, 21)]
    [InlineData(ArtefactValidationCodes.ToolMissing, 22)]
    [InlineData(ArtefactValidationCodes.CredentialInvalid, 23)]
    [InlineData(ArtefactValidationCodes.CredentialFormatRejected, 24)]
    [InlineData(ArtefactValidationCodes.ModelNotFound, 25)]
    [InlineData(ArtefactValidationCodes.ProbeTimeout, 26)]
    [InlineData(ArtefactValidationCodes.ProbeInternalError, 27)]
    public void ForCode_MapsEveryKnownValidationCodeToItsExitCode(string code, int expected)
    {
        ArtefactValidationExitCodes.ForCode(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingThatDoesNotExist")]
    [InlineData("imagePullFailed")] // case-sensitive — server emits the exact constant name
    public void ForCode_UnknownCodeMapsToUnknownError(string? code)
    {
        ArtefactValidationExitCodes.ForCode(code).ShouldBe(ArtefactValidationExitCodes.UnknownError);
    }

    [Fact]
    public void SuccessAndUsageErrorConstants_HoldTheContractValues()
    {
        // The whole public surface of the exit-code contract is additive-
        // only; these three constants are the reserved header rows that
        // operators read first from `--help`.
        ArtefactValidationExitCodes.Success.ShouldBe(0);
        ArtefactValidationExitCodes.UnknownError.ShouldBe(1);
        ArtefactValidationExitCodes.UsageError.ShouldBe(2);
    }

    [Fact]
    public void HelpTable_ListsEveryMappedCode()
    {
        // The help table is mirrored in `spring unit create --help` and
        // `spring unit revalidate --help`. If a new code is added to the
        // `ForCode` mapping but not to the table (or vice versa) operators
        // get a silent drift; pin both shapes together.
        var table = ArtefactValidationExitCodes.HelpTable;
        table.ShouldContain("Exit codes:");
        table.ShouldContain("20");
        table.ShouldContain("ImagePullFailed");
        table.ShouldContain("21");
        table.ShouldContain("ImageStartFailed");
        table.ShouldContain("22");
        table.ShouldContain("ToolMissing");
        table.ShouldContain("23");
        table.ShouldContain("CredentialInvalid");
        table.ShouldContain("24");
        table.ShouldContain("CredentialFormatRejected");
        table.ShouldContain("25");
        table.ShouldContain("ModelNotFound");
        table.ShouldContain("26");
        table.ShouldContain("ProbeTimeout");
        table.ShouldContain("27");
        table.ShouldContain("ProbeInternalError");
        table.ShouldContain("--no-wait");
    }
}
