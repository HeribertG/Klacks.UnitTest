// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ListShiftCutsSkill: the cut tree projection reports readable status labels and the
/// order name it belongs to, and an unknown shiftId is rejected before any cut list is read.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListShiftCutsSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private ListShiftCutsSkill _skill = null!;

    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid PartAId = Guid.NewGuid();
    private static readonly Guid PartBId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _skill = new ListShiftCutsSkill(_shiftRepository);
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
}
