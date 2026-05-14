// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RegisterCloneActivity"/>. Post #1629: clone ids
/// are Guids — the test passes the no-dash hex form on input and asserts the
/// emitted <see cref="DirectoryEntry.ActorId"/> matches that Guid.
/// </summary>
public class RegisterCloneActivityTests
{
    private static readonly Guid ParentGuid = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid CloneGuid = new("aaaaaaaa-1111-1111-1111-000000000002");
    private static readonly string ParentIdHex = ParentGuid.ToString("N");
    private static readonly string CloneIdHex = CloneGuid.ToString("N");

    private readonly IDirectoryService _directoryService;
    private readonly RegisterCloneActivity _activity;
    private readonly WorkflowActivityContext _context;

    public RegisterCloneActivityTests()
    {
        _directoryService = Substitute.For<IDirectoryService>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new RegisterCloneActivity(_directoryService, loggerFactory);
        _context = Substitute.For<WorkflowActivityContext>();
    }

    [Fact]
    public async Task RunAsync_RegistersCloneInDirectory()
    {
        var input = new CloningInput(
            ParentIdHex, CloneIdHex,
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.ShouldBeTrue();
        await _directoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.Address.Id == CloneGuid &&
                e.ActorId == CloneGuid &&
                e.Description.Contains("detached")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AttachedMode_IncludesAttachedInDescription()
    {
        var input = new CloningInput(
            ParentIdHex, CloneIdHex,
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        await _activity.RunAsync(_context, input);

        await _directoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Description.Contains("attached")),
            Arg.Any<CancellationToken>());
    }
}
