// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ResetShiftCutsSkill: the requested date is validated against GetResetDateRangeQuery
/// before PostResetCutsCommand is sent, an order that is not split is rejected up front, a refusal of the reset comes back
/// as a skill error, and the reset is confirmed by re-reading the cut list for one open OriginalShift, wherever it starts,
/// and no piece reaching into it.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ResetShiftCutsSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IShiftReader _shiftReader = null!;
    private IMediator _mediator = null!;
    private ResetShiftCutsSkill _skill = null!;

    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid PartId = Guid.NewGuid();
    private static readonly DateOnly OrderFrom = new(2026, 1, 1);
    private static readonly DateOnly OrderUntil = new(2026, 12, 31);

    [Test]
    public async Task Execute_AShiftWithAnAnalyseToken_IsAnsweredLikeAMissingOne_AndWritesNothing()
    {
        var keyShiftId = Guid.NewGuid();
        _shiftRepository.Get(keyShiftId).Returns(new Shift
        {
            Id = keyShiftId,
            Name = "Key order",
            Status = ShiftStatus.SealedOrder,
            AnalyseToken = Guid.NewGuid()
        });
        var parameters = new Dictionary<string, object>
        {
            ["shiftId"] = keyShiftId.ToString(),
            ["newStartDate"] = OrderFrom
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift with ID {keyShiftId} not found.");
        await _mediator.DidNotReceive().Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>());
    }

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftReader = Substitute.For<IShiftReader>();
        _mediator = Substitute.For<IMediator>();
        _skill = new ResetShiftCutsSkill(_shiftRepository, _shiftReader, _mediator, ShiftVisibilityGuards.AllowAll());

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

        ShiftLineageStub.Serve(_shiftReader, () => [part]);

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
        ShiftLineageStub.Serve(_shiftReader, () => []);

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not split"));
        await _mediator.DidNotReceive().Send(Arg.Any<GetResetDateRangeQuery>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutWork_ReportsTheRequestedStart_AndThePieceKeptBeforeIt()
    {
        var start = new DateOnly(2026, 6, 1);
        var kept = new Shift { Id = PartId, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = OrderFrom, UntilDate = start.AddDays(-1) };
        var merged = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalShift, OriginalId = SealedOrderId, FromDate = start, UntilDate = OrderUntil };
        _shiftRepository.CutList(SealedOrderId).Returns(new List<Shift> { kept, merged });

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, start));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain(start.ToString());
        result.Message.ShouldContain("1 part(s) beginning before it stay");
    }

    [Test]
    public async Task ReturnsError_WhenAPieceStillReachesIntoTheNewShift()
    {
        var merged = new Shift { Id = Guid.NewGuid(), Status = ShiftStatus.OriginalShift, OriginalId = SealedOrderId, FromDate = new DateOnly(2026, 6, 1) };
        var stale = new Shift { Id = PartId, Status = ShiftStatus.SplitShift, OriginalId = SealedOrderId, FromDate = OrderFrom, UntilDate = new DateOnly(2026, 6, 1) };
        _shiftRepository.CutList(SealedOrderId).Returns(new List<Shift> { stale, merged });

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task RelaysARefusal_AsASkillError()
    {
        var refusal = ShiftResetRefusedException.ForWorksOnOrAfter(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 3));
        _mediator.Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>()).Returns<List<ShiftResource>>(_ => throw refusal);

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(refusal.Message);
    }

    [Test]
    public async Task AHiddenRowOfTheFamily_IsAnsweredLikeAMissingShift_AtTheDateRange()
    {
        _mediator.Send(Arg.Any<GetResetDateRangeQuery>(), Arg.Any<CancellationToken>())
            .Returns<ResetDateRangeResponse>(_ => throw new KeyNotFoundException("hidden"));

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift with ID {PartId} not found.");
    }

    [Test]
    public async Task AHiddenRowOfTheFamily_IsAnsweredLikeAMissingShift_AtTheReset()
    {
        _mediator.Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>())
            .Returns<List<ShiftResource>>(_ => throw new InvalidOperationException(string.Format(ShiftVisibilityMessages.NoSealedOrderFound, SealedOrderId)));

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift with ID {PartId} not found.");
    }

    [Test]
    public async Task RelaysAContainerRefusal_AsASkillError()
    {
        _mediator.Send(Arg.Any<PostResetCutsCommand>(), Arg.Any<CancellationToken>())
            .Returns<List<ShiftResource>>(_ => throw new BadRequestException("container"));

        var result = await _skill.ExecuteAsync(Ctx(), Params(PartId, new DateOnly(2026, 6, 1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("container");
    }
}
