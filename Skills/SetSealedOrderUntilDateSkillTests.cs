// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SetSealedOrderUntilDateSkill: the family is ended through IShiftFamilyEndService and the until-date is
/// re-read from the database; a confirmed read reports a verified success, a shift that is not a sealed order or already has
/// an until-date is rejected up front, and a refusal of the service is answered as an error.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetSealedOrderUntilDateSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IShiftFamilyEndService _familyEnd = null!;
    private SetSealedOrderUntilDateSkill _skill = null!;

    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateOnly FromDate = new(2026, 6, 1);
    private static readonly DateOnly UntilDate = new(2026, 12, 31);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _familyEnd = Substitute.For<IShiftFamilyEndService>();
        _skill = new SetSealedOrderUntilDateSkill(_shiftRepository, _familyEnd, ShiftVisibilityGuards.AllowAll());
    }

    [Test]
    public async Task Execute_AShiftWithAnAnalyseToken_IsAnsweredLikeAMissingOne_AndWritesNothing()
    {
        var keyOrder = OpenEndedSealedOrder();
        keyOrder.AnalyseToken = Guid.NewGuid();
        _shiftRepository.Get(OrderId).Returns(keyOrder);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Order with ID {OrderId} not found.");
        await _familyEnd.DidNotReceiveWithAnyArgs().EndFamilyAsync(default!, default, default, default);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = OrderId.ToString(),
        ["untilDate"] = UntilDate.ToString("yyyy-MM-dd")
    };

    private static Shift OpenEndedSealedOrder() => new()
    {
        Id = OrderId,
        Status = ShiftStatus.SealedOrder,
        Name = "Reinigung Müller",
        FromDate = FromDate,
        UntilDate = null
    };

    [Test]
    public async Task EndsTheFamilyThroughTheService_AndReportsVerified()
    {
        var order = OpenEndedSealedOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _shiftRepository.GetNoTracking(OrderId)
            .Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder, UntilDate = UntilDate });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("end on the same date");
        await _familyEnd.Received(1).EndFamilyAsync(order, UntilDate, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportsAnError_WhenTheWriteCannotBeConfirmed()
    {
        _shiftRepository.Get(OrderId).Returns(OpenEndedSealedOrder());
        _shiftRepository.GetNoTracking(OrderId).Returns(OpenEndedSealedOrder());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("did not confirm");
        result.Message.ShouldNotContain("rolled back");
    }

    [Test]
    public async Task RejectsSetting_WhenUntilDateAlreadySet()
    {
        var order = OpenEndedSealedOrder();
        order.UntilDate = new DateOnly(2026, 9, 1);
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("already has an until-date");
        await _familyEnd.DidNotReceiveWithAnyArgs().EndFamilyAsync(default!, default, default, default);
    }

    [Test]
    public async Task AnswersARefusalOfTheServiceAsAnError()
    {
        var order = OpenEndedSealedOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _familyEnd.EndFamilyAsync(order, UntilDate, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(ShiftEndDateRefusedException.ForWorksAfter(UntilDate, UntilDate.AddDays(3)));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("work is planned after that date");
    }

    [Test]
    public async Task AnswersAScenarioRefusalOfTheServiceAsAnError()
    {
        var order = OpenEndedSealedOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _familyEnd.EndFamilyAsync(order, UntilDate, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(ShiftEndDateRefusedException.ForScenarioWorksAfter(UntilDate));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("a scenario holds work");
        result.Message.ShouldNotContain(order.Name);
    }

    [Test]
    public async Task AnswersAHiddenRowsRefusalOfTheServiceAsAnError()
    {
        var order = OpenEndedSealedOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _familyEnd.EndFamilyAsync(order, UntilDate, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(ShiftEndDateRefusedException.ForHiddenRows());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not all of them are visible");
    }

    [Test]
    public async Task RejectsSetting_WhenNotSealed()
    {
        var order = OpenEndedSealedOrder();
        order.Status = ShiftStatus.OriginalOrder;
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not sealed yet");
        await _familyEnd.DidNotReceiveWithAnyArgs().EndFamilyAsync(default!, default, default, default);
    }

    [Test]
    public async Task AContainer_HasNoOrder_AndEndsAsItsOwnFamily()
    {
        var container = OpenEndedSealedOrder();
        container.Status = ShiftStatus.OriginalShift;
        container.ShiftType = ShiftType.IsContainer;
        var persisted = OpenEndedSealedOrder();
        persisted.Status = ShiftStatus.OriginalShift;
        persisted.ShiftType = ShiftType.IsContainer;
        persisted.UntilDate = UntilDate;
        _shiftRepository.Get(OrderId).Returns(container);
        _shiftRepository.GetNoTracking(OrderId).Returns(persisted);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _familyEnd.Received(1).EndFamilyAsync(container, UntilDate, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RejectsADerivedRow_AndNamesTheSealedOrder()
    {
        var plannable = OpenEndedSealedOrder();
        plannable.Status = ShiftStatus.OriginalShift;
        plannable.OriginalId = Guid.NewGuid();
        _shiftRepository.Get(OrderId).Returns(plannable);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(plannable.OriginalId.Value.ToString());
        await _familyEnd.DidNotReceiveWithAnyArgs().EndFamilyAsync(default!, default, default, default);
    }
}
