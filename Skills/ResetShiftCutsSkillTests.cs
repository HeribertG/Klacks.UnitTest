// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ResetShiftCutsSkill: the requested date is validated against GetResetDateRangeQuery
/// before PostResetCutsCommand is sent, an order that is not split is rejected up front, and the merge
/// is confirmed by re-reading the cut list for a single OriginalShift and no stale open SplitShift.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ResetShiftCutsSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private ResetShiftCutsSkill _skill = null!;

    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid PartId = Guid.NewGuid();
    private static readonly DateOnly OrderFrom = new(2026, 1, 1);
    private static readonly DateOnly OrderUntil = new(2026, 12, 31);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mediator = Substitute.For<IMediator>();
        _skill = new ResetShiftCutsSkill(_shiftRepository, _mediator);

        var part = new Shift
        {
            Id = PartId,
            Name = "Frühdienst",
            Status = ShiftStatus.SplitShift,
            OriginalId = SealedOrderId,
            FromDate = OrderFrom,
            UntilDate = null
        };
        _shiftRepository.Get(PartId).Returns(part);

        var sealedOrder = new Shift { Id = SealedOrderId, Name = "24h-Schichtdienst", Status = ShiftStatus.SealedOrder, FromDate = OrderFrom, UntilDate = OrderUntil };
        _shiftRepository.GetSealedOrder(SealedOrderId).Returns(sealedOrder);

        _shiftRepository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift> { part }));

        _mediator.Send(Arg.Any<GetResetDateRangeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ResetDateRangeResponse { EarliestResetDate = OrderFrom, UntilDate = OrderUntil });

        _mediator.Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftResource>());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(Guid shiftId, DateOnly newStartDate) => new()
    {
        ["shiftId"] = shiftId.ToString(),
        ["newStartDate"] = newStartDate.ToString("yyyy-MM-dd")
    };

    [Test]
    public async Task Resets_AndReportsVerified_WhenMergeIsConfirmed()
    {
        var newStartDate = new DateOnly(2026, 6, 1);

        var mergedShift = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalShift, OriginalId = SealedOrderId, FromDate = newStartDate, UntilDate = OrderUntil };
        var closedPart = new Shift { Id = PartId, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = OrderFrom, UntilDate = newStartDate.AddDays(-1) };
        _shiftRepository.CutList(SealedOrderId).Returns(new List<Shift> { closedPart, mergedShift });

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, newStartDate));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _mediator.Received(1).Send(Arg.Is<PostResetCutsCommand>(c => c.OriginalId == SealedOrderId && c.NewStartDate == newStartDate), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenNewStartDateIsOutsideAllowedRange()
    {
        var tooEarly = OrderFrom.AddDays(-1);

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, tooEarly));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("cannot be earlier"));
        await _mediator.DidNotReceive().Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenOrderIsNotSplit()
    {
        _shiftRepository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(new List<Shift>()));

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not split"));
        await _mediator.DidNotReceive().Send(Arg.Any<GetResetDateRangeQuery>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>());
    }
}
