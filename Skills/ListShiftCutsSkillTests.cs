// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ListShiftCutsSkill: the cut tree projection reports readable status labels and the
/// order name it belongs to, and an unknown, hidden or tokened shiftId is rejected the same way before any cut list is read.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListShiftCutsSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IShiftGroupFilterService _groupFilter = null!;
    private ListShiftCutsSkill _skill = null!;

    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid PartAId = Guid.NewGuid();
    private static readonly Guid PartBId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupFilter = Substitute.For<IShiftGroupFilterService>();
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns((List<Guid>?)null);
        _skill = new ListShiftCutsSkill(_shiftRepository, ShiftVisibilityGuards.AllowAll(), _groupFilter);
    }

    [Test]
    public async Task LeavesOutAPieceOfAGroupTheCallerMayNotSee()
    {
        var visibleGroupId = Guid.NewGuid();
        var order = new Shift { Id = SealedOrderId, Name = "Order", Status = ShiftStatus.SealedOrder };
        var visible = new Shift { Id = PartAId, OriginalId = SealedOrderId, Name = "Visible", Status = ShiftStatus.SplitShift };
        visible.GroupItems.Add(new GroupItem { GroupId = visibleGroupId, ShiftId = PartAId });
        var hidden = new Shift { Id = PartBId, OriginalId = SealedOrderId, Name = "Hidden", Status = ShiftStatus.SplitShift };
        hidden.GroupItems.Add(new GroupItem { GroupId = Guid.NewGuid(), ShiftId = PartBId });
        _shiftRepository.Get(SealedOrderId).Returns(order);
        _shiftRepository.GetSealedOrder(SealedOrderId).Returns(order);
        _shiftRepository.CutList(SealedOrderId).Returns([visible, hidden]);
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns([visibleGroupId]);

        var result = await _skill.ExecuteAsync(Ctx(), Params(SealedOrderId));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("1 piece(s)");
        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldNotContain("Hidden");
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static Dictionary<string, object> Params(Guid shiftId) => new()
    {
        ["shiftId"] = shiftId.ToString()
    };

    [Test]
    public async Task ReturnsCompactPieces_WithReadableStatusLabels()
    {
        var sealedOrder = new Shift { Id = SealedOrderId, Name = "24h-Schichtdienst", Status = ShiftStatus.SealedOrder };

        var partA = new Shift
        {
            Id = PartAId,
            Name = "Frühdienst",
            Abbreviation = "FD",
            Status = ShiftStatus.SplitShift,
            OriginalId = SealedOrderId,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(7, 0),
            EndShift = new TimeOnly(15, 0),
            IsMonday = true,
            IsTuesday = true
        };

        var partB = new Shift
        {
            Id = PartBId,
            Name = "Spätdienst",
            Abbreviation = "SD",
            Status = ShiftStatus.OriginalShift,
            OriginalId = SealedOrderId,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(15, 0),
            EndShift = new TimeOnly(23, 0)
        };

        _shiftRepository.Get(PartAId).Returns(partA);
        _shiftRepository.GetSealedOrder(SealedOrderId).Returns(sealedOrder);
        _shiftRepository.CutList(PartAId).Returns(new List<Shift> { partA, partB });

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartAId));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("24h-Schichtdienst"));
        Assert.That(result.Message, Does.Contain("2 piece"));
    }

    [Test]
    public async Task ReturnsEmptyMessage_WhenOrderHasNoSplitParts()
    {
        var loadedShift = new Shift { Id = PartAId, Name = "Order X", Status = ShiftStatus.OriginalShift, OriginalId = SealedOrderId };

        _shiftRepository.Get(PartAId).Returns(loadedShift);
        _shiftRepository.GetSealedOrder(SealedOrderId).Returns((Shift?)null);
        _shiftRepository.CutList(PartAId).Returns(new List<Shift>());

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartAId));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("no split parts"));
    }

    [Test]
    public async Task ReturnsError_WhenShiftNotFound()
    {
        _shiftRepository.Get(PartAId).Returns((Shift?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartAId));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
        await _shiftRepository.DidNotReceive().CutList(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<bool>());
    }

    [Test]
    public async Task AHiddenShift_IsAnsweredLikeAMissingOne_WithoutItsName()
    {
        _shiftRepository.Get(PartAId).Returns(new Shift { Id = PartAId, Name = "Secret order", Status = ShiftStatus.OriginalShift });
        var skill = new ListShiftCutsSkill(_shiftRepository, ShiftVisibilityGuards.Hiding(PartAId), _groupFilter);

        var result = await skill.ExecuteAsync(Ctx(), Params(PartAId));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.EqualTo($"Shift with ID {PartAId} not found."));
        await _shiftRepository.DidNotReceive().CutList(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<bool>());
    }

    [Test]
    public async Task AShiftWithAToken_IsAnsweredLikeAMissingOne_ForAnUnrestrictedCallerToo()
    {
        _shiftRepository.Get(PartAId).Returns(new Shift
        {
            Id = PartAId,
            Name = "Confidential key shift",
            Status = ShiftStatus.SealedOrder,
            AnalyseToken = Guid.NewGuid()
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartAId));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.EqualTo($"Shift with ID {PartAId} not found."));
        await _shiftRepository.DidNotReceive().CutList(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<bool>());
    }
}
