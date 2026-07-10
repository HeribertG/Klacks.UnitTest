// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CutShiftByDateSkill: cutting the plannable shift of an uncut order produces an
/// UPDATE (shrunk original, status forced to SplitShift) and a CREATE (new piece from cutDate) batch,
/// an order id is resolved to its plannable shift like cut_shift does, and a cutDate outside the
/// piece's own date range is rejected before the batch is sent.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CutShiftByDateSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IShiftCutFacade _shiftCutFacade = null!;
    private CutShiftByDateSkill _skill = null!;

    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid OriginalShiftId = Guid.NewGuid();
    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 12, 31);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftCutFacade = Substitute.For<IShiftCutFacade>();
        _skill = new CutShiftByDateSkill(_shiftRepository, _shiftCutFacade);

        _shiftRepository.GetGroupsForShift(Arg.Any<Guid>()).Returns(new List<Group>());
        _shiftCutFacade.ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>()).Returns(new List<Shift>());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(Guid shiftId, DateOnly cutDate) => new()
    {
        ["shiftId"] = shiftId.ToString(),
        ["cutDate"] = cutDate.ToString("yyyy-MM-dd")
    };

    private static Shift MakeOriginalShift() => new()
    {
        Id = OriginalShiftId,
        Name = "24h-Schichtdienst",
        Abbreviation = "24H",
        Status = ShiftStatus.OriginalShift,
        OriginalId = SealedOrderId,
        FromDate = FromDate,
        UntilDate = UntilDate,
        StartShift = new TimeOnly(7, 0),
        EndShift = new TimeOnly(7, 0)
    };

    [Test]
    public async Task Cuts_AndReportsVerified_WhenBothPiecesAreConfirmed()
    {
        var cutDate = new DateOnly(2026, 6, 1);
        var target = MakeOriginalShift();
        _shiftRepository.Get(OriginalShiftId).Returns(target);

        List<CutOperation>? capturedOps = null;
        _shiftCutFacade.ProcessBatchCutsAsync(Arg.Do<List<CutOperation>>(ops => capturedOps = ops))
            .Returns(new List<Shift>());

        _shiftRepository.CutList(SealedOrderId).Returns(ci =>
        {
            var updated = new Shift { Id = OriginalShiftId, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = FromDate, UntilDate = cutDate.AddDays(-1) };
            var created = capturedOps!.First(o => o.Type == "CREATE").Data;
            var newPiece = new Shift { Id = created.Id, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = created.FromDate, UntilDate = created.UntilDate };
            return new List<Shift> { updated, newPiece };
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, cutDate));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));

        Assert.That(capturedOps, Is.Not.Null);
        Assert.That(capturedOps!.Count, Is.EqualTo(2));

        var updateOp = capturedOps.First(o => o.Type == "UPDATE");
        Assert.That(updateOp.Data.Id, Is.EqualTo(OriginalShiftId));
        Assert.That(updateOp.Data.UntilDate, Is.EqualTo(cutDate.AddDays(-1)));
        Assert.That(updateOp.Data.Status, Is.EqualTo(ShiftStatus.SplitShift));

        var createOp = capturedOps.First(o => o.Type == "CREATE");
        Assert.That(createOp.Data.FromDate, Is.EqualTo(cutDate));
        Assert.That(createOp.Data.UntilDate, Is.EqualTo(UntilDate));
        Assert.That(createOp.ParentId, Is.EqualTo(SealedOrderId.ToString()));
    }

    [Test]
    public async Task ResolvesOrderIdToPlannableShift_LikeCutShift()
    {
        var cutDate = new DateOnly(2026, 6, 1);
        var sealedOrder = new Shift { Id = SealedOrderId, Name = "24h-Schichtdienst", Status = ShiftStatus.SealedOrder, FromDate = FromDate, UntilDate = UntilDate };
        var target = MakeOriginalShift();

        _shiftRepository.Get(SealedOrderId).Returns(sealedOrder);
        _shiftRepository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { target }));

        List<CutOperation>? capturedOps = null;
        _shiftCutFacade.ProcessBatchCutsAsync(Arg.Do<List<CutOperation>>(ops => capturedOps = ops))
            .Returns(new List<Shift>());

        _shiftRepository.CutList(SealedOrderId).Returns(ci =>
        {
            var updated = new Shift { Id = OriginalShiftId, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = FromDate, UntilDate = cutDate.AddDays(-1) };
            var created = capturedOps!.First(o => o.Type == "CREATE").Data;
            var newPiece = new Shift { Id = created.Id, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = created.FromDate, UntilDate = created.UntilDate };
            return new List<Shift> { updated, newPiece };
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params(SealedOrderId, cutDate));

        Assert.That(result.Success, Is.True);
        Assert.That(capturedOps, Is.Not.Null);
        var updateOp = capturedOps!.First(o => o.Type == "UPDATE");
        Assert.That(updateOp.Data.Id, Is.EqualTo(OriginalShiftId));
        await _shiftCutFacade.Received(1).ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>());
    }

    [Test]
    public async Task ReturnsError_WhenCutDateIsOutsideRange()
    {
        var target = MakeOriginalShift();
        _shiftRepository.Get(OriginalShiftId).Returns(target);

        var tooLate = UntilDate.AddDays(1);

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, tooLate));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("must be strictly after"));
        await _shiftCutFacade.DidNotReceive().ProcessBatchCutsAsync(Arg.Any<List<CutOperation>>());
    }

    [Test]
    public async Task ReturnsError_WhenShiftNotFound()
    {
        _shiftRepository.Get(OriginalShiftId).Returns((Shift?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params(OriginalShiftId, new DateOnly(2026, 6, 1)));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }
}
