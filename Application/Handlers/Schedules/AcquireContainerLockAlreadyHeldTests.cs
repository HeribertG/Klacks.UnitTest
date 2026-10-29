// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

/// <summary>
/// AlreadyHeld tells a caller that the acquired lock is the one the same user and instance (the open container page in
/// the same tab) already held, so the caller must not release it; a fresh or refused lock answers false.
/// </summary>
[TestFixture]
public class AcquireContainerLockAlreadyHeldTests
{
    private const string InstanceId = "tab-1";

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _containerId = Guid.NewGuid();
    private IContainerLockRepository _repository = null!;
    private AcquireContainerLockCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IContainerLockRepository>();
        var userService = Substitute.For<IUserService>();
        userService.GetId().Returns(_userId);
        userService.GetUserName().Returns("planner");

        _handler = new AcquireContainerLockCommandHandler(
            _repository,
            userService,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IContainerVisibilityGuard>(),
            Substitute.For<IContainerWorkChildrenReadRepository>(),
            Substitute.For<IScheduleRowAccessGuard>(),
            Substitute.For<IShiftVisibilityGuard>(),
            Substitute.For<ILogger<AcquireContainerLockCommandHandler>>());
    }

    [Test]
    public async Task LockOfTheSameUserAndInstance_IsReturnedAsAlreadyHeld()
    {
        // Arrange
        StoredLock(_userId, InstanceId);

        // Act
        var result = await _handler.Handle(Command(InstanceId), CancellationToken.None);

        // Assert
        result.Acquired.ShouldBeTrue();
        result.AlreadyHeld.ShouldBeTrue();
    }

    [Test]
    public async Task NewLock_IsNotAlreadyHeld()
    {
        // Arrange
        _repository.GetByResource(ContainerLockResourceTypes.ContainerTemplate, _containerId, Arg.Any<CancellationToken>())
            .Returns((ContainerLock?)null);

        // Act
        var result = await _handler.Handle(Command(InstanceId), CancellationToken.None);

        // Assert
        result.Acquired.ShouldBeTrue();
        result.AlreadyHeld.ShouldBeFalse();
    }

    [Test]
    public async Task LockOfAnotherInstance_IsRefusedAndNotAlreadyHeld()
    {
        // Arrange
        StoredLock(_userId, "tab-2");

        // Act
        var result = await _handler.Handle(Command(InstanceId), CancellationToken.None);

        // Assert
        result.Acquired.ShouldBeFalse();
        result.IsSelfConflict.ShouldBeTrue();
        result.AlreadyHeld.ShouldBeFalse();
    }

    private AcquireContainerLockCommand Command(string instanceId) =>
        new(ContainerLockResourceTypes.ContainerTemplate, _containerId, instanceId);

    private void StoredLock(Guid userId, string instanceId)
    {
        _repository.GetByResource(ContainerLockResourceTypes.ContainerTemplate, _containerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerLock
            {
                Id = Guid.NewGuid(),
                ResourceType = ContainerLockResourceTypes.ContainerTemplate,
                ResourceId = _containerId,
                UserId = userId,
                UserName = "planner",
                InstanceId = instanceId,
                AcquiredAt = DateTime.UtcNow,
                LastHeartbeatAt = DateTime.UtcNow
            });
    }
}
