// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The scope releases only a lock it created, never the reused lock of the open container page, and never a lock it
/// did not get; the release runs without the caller's token and a failing release does not escape.
/// </summary>
[TestFixture]
public class ContainerTemplateLockScopeTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Test]
    public async Task FreshLock_IsReleasedWithoutTheCallersToken()
    {
        // Arrange
        Lock(acquired: true, alreadyHeld: false);
        using var cts = new CancellationTokenSource();

        // Act
        await using (var scope = await ContainerTemplateLockScope.AcquireAsync(_mediator, ContainerId, "tab-1", cts.Token))
        {
            scope.Acquired.ShouldBeTrue();
        }

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<AcquireContainerLockCommand>(c => c.ResourceId == ContainerId && c.InstanceId == "tab-1"), cts.Token);
        await _mediator.Received(1).Send(Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), CancellationToken.None);
    }

    [Test]
    public async Task ReusedLock_IsNotReleased()
    {
        // Arrange
        Lock(acquired: true, alreadyHeld: true);

        // Act
        await using (var scope = await ContainerTemplateLockScope.AcquireAsync(_mediator, ContainerId, "tab-1", CancellationToken.None))
        {
            scope.AlreadyHeld.ShouldBeTrue();
        }

        // Assert
        await _mediator.DidNotReceive().Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefusedLock_IsNotReleased()
    {
        // Arrange
        Lock(acquired: false, alreadyHeld: false);

        // Act
        await using (var scope = await ContainerTemplateLockScope.AcquireAsync(_mediator, ContainerId, "tab-1", CancellationToken.None))
        {
            scope.Acquired.ShouldBeFalse();
        }

        // Assert
        await _mediator.DidNotReceive().Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingRelease_DoesNotEscape()
    {
        // Arrange
        Lock(acquired: true, alreadyHeld: false);
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("db down"));

        // Act
        var scope = await ContainerTemplateLockScope.AcquireAsync(_mediator, ContainerId, "tab-1", CancellationToken.None);

        // Assert
        await Should.NotThrowAsync(async () => await scope.DisposeAsync());
    }

    private void Lock(bool acquired, bool alreadyHeld) =>
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = acquired, AlreadyHeld = alreadyHeld });
}
