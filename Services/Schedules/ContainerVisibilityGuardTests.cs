// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container visibility guard answers a hidden container and a missing one with the same KeyNotFoundException
/// text, and lets a write bring in only visible task shifts: hidden or missing task shifts the container already
/// references (in its templates or overrides) stay allowed, so a container mixing groups stays saveable, and the stored
/// baseline is only read when the requested set contains a shift the caller may not touch. ExistsAndVisibleAsync of the
/// shift guard asks exactly one shift query for every caller: existence for an unrestricted caller, visibility for a
/// restricted one.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ContainerVisibilityGuardTests
{
    private const string IdPlaceholder = "<id>";

    private IShiftVisibilityReader _reader = null!;
    private Guid _containerId;
    private Guid _hiddenShiftId;

    [SetUp]
    public void SetUp()
    {
        _reader = Substitute.For<IShiftVisibilityReader>();
        _containerId = Guid.NewGuid();
        _hiddenShiftId = Guid.NewGuid();
    }

    private ContainerVisibilityGuard Guard(IShiftVisibilityGuard shiftGuard) =>
        new(shiftGuard, _reader, Substitute.For<ILogger<ContainerVisibilityGuard>>());

    [Test]
    public async Task EnsureContainerVisible_HiddenAndMissing_ThrowTheSameText()
    {
        var missingId = Guid.NewGuid();
        var shiftGuard = ShiftVisibilityGuards.Hiding(_containerId, missingId);

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => Guard(shiftGuard).EnsureContainerVisibleAsync(_containerId));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => Guard(shiftGuard).EnsureContainerVisibleAsync(missingId));

        hidden.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _containerId));
        hidden.Message.Replace(_containerId.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingId.ToString(), IdPlaceholder));
    }

    [Test]
    public async Task EnsureContainerVisible_VisibleContainer_Passes()
    {
        await Should.NotThrowAsync(() => Guard(ShiftVisibilityGuards.AllowAll()).EnsureContainerVisibleAsync(_containerId));
    }

    [Test]
    public async Task EnsureItemShifts_AllVisible_PassesWithoutReadingTheStoredBaseline()
    {
        await Guard(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .EnsureItemShiftsVisibleAsync(_containerId, [Guid.NewGuid(), null, Guid.NewGuid()]);

        await _reader.DidNotReceiveWithAnyArgs().GetContainerItemShiftIdsAsync(default, default);
    }

    [Test]
    public async Task EnsureItemShifts_NewHiddenShift_IsRefusedLikeAMissingShift()
    {
        _reader.GetContainerItemShiftIdsAsync(_containerId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => Guard(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .EnsureItemShiftsVisibleAsync(_containerId, [Guid.NewGuid(), _hiddenShiftId]));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
    }

    [Test]
    public async Task EnsureItemShifts_HiddenShiftTheContainerAlreadyReferences_StaysAllowed()
    {
        _reader.GetContainerItemShiftIdsAsync(_containerId, Arg.Any<CancellationToken>()).Returns([_hiddenShiftId]);

        await Should.NotThrowAsync(() => Guard(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .EnsureItemShiftsVisibleAsync(_containerId, [_hiddenShiftId, Guid.NewGuid()]));
    }

    [Test]
    public async Task EnsureItemShifts_OnlyAbsenceItems_PassWithoutAnyQuery()
    {
        var shiftGuard = ShiftVisibilityGuards.HideAll();

        await Guard(shiftGuard).EnsureItemShiftsVisibleAsync(_containerId, [null, null]);

        await shiftGuard.DidNotReceiveWithAnyArgs().FilterVisibleAsync(default!, default);
        await _reader.DidNotReceiveWithAnyArgs().GetContainerItemShiftIdsAsync(default, default);
    }

    [Test]
    public async Task ShiftGuard_ExistsAndVisible_Unrestricted_AsksOnlyForExistence()
    {
        var filter = Substitute.For<IShiftGroupFilterService>();
        filter.ResolveShiftListGroupIdsAsync(null).Returns(Task.FromResult<List<Guid>?>(null));
        _reader.ExistsAsync(_containerId, Arg.Any<CancellationToken>()).Returns(true);
        var guard = new ShiftVisibilityGuard(
            filter, _reader, Substitute.For<IGroupVisibilityGuard>(), Substitute.For<ILogger<ShiftVisibilityGuard>>());

        (await guard.ExistsAndVisibleAsync(_containerId)).ShouldBeTrue();
        (await guard.ExistsAndVisibleAsync(Guid.NewGuid())).ShouldBeFalse();

        await _reader.DidNotReceiveWithAnyArgs().FilterVisibleShiftIdsAsync(default!, default!, default);
    }

    [Test]
    public async Task ShiftGuard_ExistsAndVisible_Restricted_AsksOnlyTheVisibilityQuery()
    {
        var visibleGroup = Guid.NewGuid();
        var filter = Substitute.For<IShiftGroupFilterService>();
        filter.ResolveShiftListGroupIdsAsync(null).Returns(Task.FromResult<List<Guid>?>([visibleGroup]));
        _reader.FilterVisibleShiftIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0).Where(id => id == _containerId).ToList());
        var guard = new ShiftVisibilityGuard(
            filter, _reader, Substitute.For<IGroupVisibilityGuard>(), Substitute.For<ILogger<ShiftVisibilityGuard>>());

        (await guard.ExistsAndVisibleAsync(_containerId)).ShouldBeTrue();
        (await guard.ExistsAndVisibleAsync(_hiddenShiftId)).ShouldBeFalse();

        await _reader.DidNotReceiveWithAnyArgs().ExistsAsync(default, default);
    }
}
