// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// optimize_container_route: validates the weekday and the container, maps ISO Sunday to storage weekday 0, passes the
/// caller's instance id to the write service and reports its outcome.
/// </summary>
[TestFixture]
public class OptimizeContainerRouteSkillTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid VisitId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IUserService _userService = null!;
    private IContainerRouteWriteService _writeService = null!;
    private OptimizeContainerRouteSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _userService = Substitute.For<IUserService>();
        _writeService = Substitute.For<IContainerRouteWriteService>();
        _userService.GetInstanceId().Returns("tab-1");
        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, Name = "Spitex Nord", ShiftType = ShiftType.IsContainer });
        _skill = new OptimizeContainerRouteSkill(_shiftRepository, _userService, ShiftVisibilityGuards.AllowAll(), _writeService);
    }

    [Test]
    public async Task Success_WritesThroughTheServiceAndReportsTheStops()
    {
        // Arrange
        _writeService.OptimizeAndWriteAsync(ContainerId, 0, true, "tab-1", Arg.Any<CancellationToken>())
            .Returns(new ContainerRouteWriteResult(
                null, "Base", "Base", 4.25, "02:10", false,
                [new ContainerRouteStop(1, VisitId, "Client", "Street 1", "08:15", "09:00", "00:10")], 1));

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(7, true));

        // Assert
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Spitex Nord");
        result.Message.ShouldContain("4.25 km");
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("reload");
    }

    [Test]
    public async Task ServiceFailure_IsReturnedAsError()
    {
        // Arrange
        _writeService.OptimizeAndWriteAsync(ContainerId, 4, false, "tab-1", Arg.Any<CancellationToken>())
            .Returns(ContainerRouteWriteResult.Failure("Nothing was written."));

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, false));

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing was written.");
    }

    [TestCase(0)]
    [TestCase(8)]
    public async Task WeekdayOutOfRange_IsRefusedWithoutWriting(int weekday)
    {
        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(weekday, false));

        // Assert
        result.Success.ShouldBeFalse();
        await _writeService.DidNotReceiveWithAnyArgs().OptimizeAndWriteAsync(default, default, default, default!, default);
    }

    [Test]
    public async Task ShiftThatIsNoContainer_IsRefusedWithoutWriting()
    {
        // Arrange
        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, ShiftType = ShiftType.IsTask });

        // Act
        var result = await _skill.ExecuteAsync(Ctx(), Params(4, false));

        // Assert
        result.Success.ShouldBeFalse();
        await _writeService.DidNotReceiveWithAnyArgs().OptimizeAndWriteAsync(default, default, default, default!, default);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(int weekday, bool isHoliday) => new()
    {
        ["containerId"] = ContainerId.ToString(),
        ["weekday"] = weekday,
        ["isHoliday"] = isHoliday
    };
}
