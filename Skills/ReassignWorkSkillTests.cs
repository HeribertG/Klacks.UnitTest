// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ReassignWorkSkill: the work is found by id or by employee and date, the hand-over goes through
/// ReassignWorkClientCommand only, a locked work, a container child, a sealed day, a hidden work or a hidden target
/// employee are refused before anything is sent, a collision at the target is reported as a warning but does not
/// stop the hand-over, and the result carries the source client id the undo needs.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ReassignWorkSkillTests
{
    private static readonly DateOnly Day = new(2026, 10, 14);
    private static readonly Guid SourceClientId = Guid.NewGuid();
    private static readonly Guid TargetClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IWorkRepository _workRepository = null!;
    private IScheduleRowAccessGuard _rowAccessGuard = null!;
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private IShiftRepository _shiftRepository = null!;
    private ICompanyClock _clock = null!;
    private IClientVisibilityGuard _visibility = null!;
    private Work _work = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _workRepository = Substitute.For<IWorkRepository>();
        _rowAccessGuard = Substitute.For<IScheduleRowAccessGuard>();
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _sealedDays = Substitute.For<ISealedDayRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _clock = Substitute.For<ICompanyClock>();
        _visibility = SkillClientVisibility.AllVisible();

        _work = NewWork();
        _workRepository.GetNoTracking(_work.Id).Returns(_work);
        _rowAccessGuard.AreAllVisibleAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clientRepository.Exists(TargetClientId).Returns(true);
        _clientRepository.Get(TargetClientId).Returns(new Client { Id = TargetClientId, FirstName = "Anna", Name = "Meier" });
        _clientRepository.Get(SourceClientId).Returns(new Client { Id = SourceClientId, FirstName = "Max", Name = "Müller" });
        _sealedDays.IsDayLockedAsync(Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _shiftRepository.Get(ShiftId).Returns(new Shift { Id = ShiftId, Name = "Early", Abbreviation = "E" });
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ReassignWorkClientResponse { Work = new WorkResource { Id = _work.Id, ClientId = TargetClientId } });
        GivenSearchFinds("Max", "Müller", SourceClientId, 11);
        GivenSearchFinds("Anna", "Meier", TargetClientId, 22);
    }

    private ReassignWorkSkill NewSkill() => new(
        _mediator, _workRepository, _rowAccessGuard, _clientRepository, _searchRepository, _visibility,
        _sealedDays, _conflictChecker, _shiftRepository, _clock);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSchedule" }
    };

    private static Work NewWork(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = SourceClientId,
        ShiftId = ShiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(6, 0),
        EndTime = new TimeOnly(14, 0)
    };

    private void GivenSearchFinds(string firstName, string lastName, Guid id, int idNumber)
    {
        _searchRepository.SearchAsync(
                Arg.Is<string?>(term => term == $"{firstName} {lastName}"), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = id, FirstName = firstName, LastName = lastName, IdNumber = idNumber }],
                TotalCount = 1
            });
    }

    private Dictionary<string, object> ByWorkId() => new()
    {
        ["workId"] = _work.Id.ToString(),
        ["targetFirstName"] = "Anna",
        ["targetLastName"] = "Meier"
    };

    private static Dictionary<string, object> ByNames() => new()
    {
        ["firstName"] = "Max",
        ["lastName"] = "Müller",
        ["date"] = "2026-10-14",
        ["targetFirstName"] = "Anna",
        ["targetLastName"] = "Meier"
    };

    private async Task AssertNothingSent(SkillResult result)
    {
        result.Success.ShouldBeFalse(result.Message);
        await _mediator.DidNotReceive().Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ByWorkId_HandsTheWorkOver_ThroughTheCommand_AndReportsTheSourceClient()
    {
        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<ReassignWorkClientCommand>(c => c.Id == _work.Id && c.TargetClientId == TargetClientId),
            Arg.Any<CancellationToken>());
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain(SourceClientId.ToString());
        json.ShouldContain(TargetClientId.ToString());
        json.ShouldContain("SourceClientId");
        json.ShouldContain("WorkId");
    }

    [Test]
    public async Task ByNamesAndDate_FindsTheOneWorkOfThatDay_AndHandsItOver()
    {
        _workRepository.GetByClientAndDateRangeAsync(
                SourceClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([_work]);
        _workRepository.GetNoTracking(_work.Id).Returns(_work);

        var result = await NewSkill().ExecuteAsync(Ctx(), ByNames());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<ReassignWorkClientCommand>(c => c.Id == _work.Id && c.TargetClientId == TargetClientId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ByNamesAndDate_NoWorkThatDay_ReportsIt_AndSendsNothing()
    {
        _workRepository.GetByClientAndDateRangeAsync(
                SourceClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await NewSkill().ExecuteAsync(Ctx(), ByNames());

        await AssertNothingSent(result);
        result.Message.ShouldContain("2026-10-14");
    }

    [Test]
    public async Task ByNamesAndDate_SeveralWorksThatDay_ListsThemAndAsksForWorkId()
    {
        var second = NewWork();
        second.StartTime = new TimeOnly(14, 0);
        second.EndTime = new TimeOnly(22, 0);
        _workRepository.GetByClientAndDateRangeAsync(
                SourceClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([_work, second]);

        var result = await NewSkill().ExecuteAsync(Ctx(), ByNames());

        await AssertNothingSent(result);
        result.Message.ShouldContain(_work.Id.ToString());
        result.Message.ShouldContain(second.Id.ToString());
        result.Message.ShouldContain("workId");
        result.Message.ShouldContain("Early");
    }

    [Test]
    public async Task ByNamesAndDate_IgnoresContainerChildrenAndScenarioWorks()
    {
        var child = NewWork();
        child.ParentWorkId = Guid.NewGuid();
        var scenarioWork = NewWork();
        scenarioWork.AnalyseToken = Guid.NewGuid();
        _workRepository.GetByClientAndDateRangeAsync(
                SourceClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([child, scenarioWork, _work]);

        var result = await NewSkill().ExecuteAsync(Ctx(), ByNames());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<ReassignWorkClientCommand>(c => c.Id == _work.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ByNames_InvalidDate_IsRefused()
    {
        var parameters = ByNames();
        parameters["date"] = "not-a-date";

        var result = await NewSkill().ExecuteAsync(Ctx(), parameters);

        await AssertNothingSent(result);
        result.Message.ShouldContain("date");
    }

    [Test]
    public async Task NoWorkIdentification_AsksForIt()
    {
        var result = await NewSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["targetFirstName"] = "Anna",
            ["targetLastName"] = "Meier"
        });

        await AssertNothingSent(result);
        result.Message.ShouldContain("workId");
    }

    [Test]
    public async Task NoTarget_AsksForIt()
    {
        var result = await NewSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = _work.Id.ToString()
        });

        await AssertNothingSent(result);
        result.Message.ShouldContain("target");
    }

    [Test]
    public async Task HiddenTargetEmployee_AnswersLikeAnUnknownOne()
    {
        _visibility = SkillClientVisibility.Hiding(TargetClientId);
        var hiddenParameters = new Dictionary<string, object>
        {
            ["workId"] = _work.Id.ToString(),
            ["targetClientId"] = TargetClientId.ToString()
        };
        var unknownId = Guid.NewGuid();
        var unknownParameters = new Dictionary<string, object>
        {
            ["workId"] = _work.Id.ToString(),
            ["targetClientId"] = unknownId.ToString()
        };

        var hidden = await NewSkill().ExecuteAsync(Ctx(), hiddenParameters);
        var unknown = await NewSkill().ExecuteAsync(Ctx(), unknownParameters);

        await AssertNothingSent(hidden);
        unknown.Success.ShouldBeFalse();
        SkillClientVisibility.WithoutId(hidden.Message, TargetClientId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Message, unknownId));
    }

    [Test]
    public async Task HiddenSourceWork_AnswersLikeAnUnknownWork_WithoutRevealingItsLockLevel()
    {
        _work.LockLevel = WorkLockLevel.Confirmed;
        _rowAccessGuard.AreAllVisibleAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var unknownId = Guid.NewGuid();
        _workRepository.GetNoTracking(unknownId).Returns((Work?)null);
        var unknownParameters = ByWorkId();
        unknownParameters["workId"] = unknownId.ToString();

        var hidden = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());
        var unknown = await NewSkill().ExecuteAsync(Ctx(), unknownParameters);

        await AssertNothingSent(hidden);
        hidden.Message.ShouldNotContain("Confirmed");
        hidden.Message!.Replace(_work.Id.ToString(), "<id>")
            .ShouldBe(unknown.Message!.Replace(unknownId.ToString(), "<id>"));
    }

    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task LockedWork_IsRefused_NamingTheLevelAndTheWayOut(WorkLockLevel level)
    {
        _work.LockLevel = level;

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        await AssertNothingSent(result);
        result.Message.ShouldContain(level.ToString());
        result.Message.ShouldContain("unconfirm_work");
    }

    [Test]
    public async Task ContainerChild_IsRefused_TheContainerHasToMove()
    {
        _work.ParentWorkId = Guid.NewGuid();

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        await AssertNothingSent(result);
        result.Message.ShouldContain("container");
    }

    [Test]
    public async Task SameEmployee_IsRefused()
    {
        var parameters = ByWorkId();
        parameters["targetFirstName"] = "Max";
        parameters["targetLastName"] = "Müller";

        var result = await NewSkill().ExecuteAsync(Ctx(), parameters);

        await AssertNothingSent(result);
        result.Message.ShouldContain("already");
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task SealedDay_ForEitherEmployee_IsRefused_EvenForAScenarioWork(bool sourceSealed, bool targetSealed)
    {
        _work.AnalyseToken = Guid.NewGuid();
        _sealedDays.IsDayLockedAsync(Day, SourceClientId, Arg.Any<CancellationToken>()).Returns(sourceSealed);
        _sealedDays.IsDayLockedAsync(Day, TargetClientId, Arg.Any<CancellationToken>()).Returns(targetSealed);

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        await AssertNothingSent(result);
        result.Message.ShouldContain("sealed");
    }

    [Test]
    public async Task CollisionAtTheTarget_IsReportedAsAWarning_AndTheWorkIsStillHandedOver()
    {
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(
            [
                new ScheduleValidationNotificationDto
                {
                    Type = ScheduleValidationType.Error,
                    ClientId = TargetClientId,
                    Date = Day,
                    Comment = "schedule.error-list.collision"
                }
            ]));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("warning");
        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldContain("schedule.error-list.collision");
        await _mediator.Received(1).Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheCollisionCheck_VacatesTheSourceRow_LikeTheHandlerDoes()
    {
        await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == TargetClientId),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(rows => rows.Count == 1 && rows[0].WorkId == _work.Id),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HardBlockingRefusalOfTheHandler_IsReportedAsNotHandedOver()
    {
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns<ReassignWorkClientResponse?>(_ => throw new ConflictException("Reassignment blocked: missing qualification."));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not handed over");
        result.Message.ShouldContain("missing qualification");
    }

    [Test]
    public async Task LockRefusalOfTheHandler_IsReportedAsNotHandedOver()
    {
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns<ReassignWorkClientResponse?>(_ => throw new InvalidRequestException("Day is sealed."));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not handed over");
        result.Message.ShouldContain("Day is sealed.");
    }

    private void GivenSearchFindsTwo(string firstName, string lastName, (Guid Id, int IdNumber) first, (Guid Id, int IdNumber) second)
    {
        _searchRepository.SearchAsync(
                Arg.Is<string?>(term => term == $"{firstName} {lastName}"), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items =
                [
                    new ClientSearchItem { Id = first.Id, FirstName = firstName, LastName = lastName, IdNumber = first.IdNumber },
                    new ClientSearchItem { Id = second.Id, FirstName = firstName, LastName = lastName, IdNumber = second.IdNumber }
                ],
                TotalCount = 2
            });
    }

    [Test]
    public async Task TwoTargetsWithTheSameName_AskForTargetIdNumber_NotForTheHoldersIdNumber()
    {
        GivenSearchFindsTwo("Anna", "Meier", (TargetClientId, 22), (Guid.NewGuid(), 23));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        await AssertNothingSent(result);
        result.Message.ShouldContain("'targetIdNumber'");
        result.Message.ShouldNotContain("'idNumber'");
    }

    [Test]
    public async Task TargetIdNumber_PicksTheTargetAmongSameNamedEmployees()
    {
        GivenSearchFindsTwo("Anna", "Meier", (Guid.NewGuid(), 21), (TargetClientId, 22));
        var parameters = ByWorkId();
        parameters["targetIdNumber"] = 22;

        var result = await NewSkill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<ReassignWorkClientCommand>(c => c.TargetClientId == TargetClientId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWrongTargetIdNumber_NamesTheTargetParameterToCorrect()
    {
        GivenSearchFindsTwo("Anna", "Meier", (TargetClientId, 22), (Guid.NewGuid(), 23));
        var parameters = ByWorkId();
        parameters["targetIdNumber"] = 99;

        var result = await NewSkill().ExecuteAsync(Ctx(), parameters);

        await AssertNothingSent(result);
        result.Message.ShouldContain("'targetIdNumber'");
        result.Message.ShouldNotContain("'idNumber'");
    }

    [Test]
    public async Task TwoHoldersWithTheSameName_StillAskForIdNumber()
    {
        GivenSearchFindsTwo("Max", "Müller", (SourceClientId, 11), (Guid.NewGuid(), 12));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByNames());

        await AssertNothingSent(result);
        result.Message.ShouldContain("'idNumber'");
        result.Message.ShouldNotContain("targetIdNumber");
    }

    [Test]
    public async Task HandlerFailedBeforeCommit_WorkStillWithTheHolder_ReportsNothingChanged()
    {
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns<ReassignWorkClientResponse?>(_ => throw new InvalidRequestException("The target is outside its membership."));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not handed over");
        result.Message.ShouldContain("Nothing was changed");
        result.Message.ShouldContain("outside its membership");
    }

    [Test]
    public async Task HandlerFailedAfterCommit_WorkAlreadyWithTheTarget_ReportsItWasHandedOverAndTheFollowUpFailed()
    {
        var committed = NewWork(_work.Id);
        committed.ClientId = TargetClientId;
        _workRepository.GetNoTracking(_work.Id).Returns(_work, committed);
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns<ReassignWorkClientResponse?>(_ => throw new InvalidRequestException(
                "An unexpected error occurred during reassigning work to another client: period hours failed"));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WAS handed over");
        result.Message.ShouldContain("follow-up");
        result.Message.ShouldContain("period hours failed");
        result.Message.ShouldNotContain("not handed over");
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("FollowUpFailed");
        json.ShouldContain(SourceClientId.ToString());
        json.ShouldContain(TargetClientId.ToString());
    }

    [Test]
    public async Task NotFoundAfterTheCommit_WorkAlreadyWithTheTarget_ReportsItWasHandedOver()
    {
        var committed = NewWork(_work.Id);
        committed.ClientId = TargetClientId;
        _workRepository.GetNoTracking(_work.Id).Returns(_work, committed);
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns<ReassignWorkClientResponse?>(_ => throw new KeyNotFoundException("lookup in the recalculation"));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WAS handed over");
    }

    [Test]
    public async Task WorkVanishedBeforeTheHandler_IsReportedAsNotFound()
    {
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns<ReassignWorkClientResponse?>(_ => throw new KeyNotFoundException("gone"));

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task EmptyHandlerResult_IsAnError_NeverASuccess()
    {
        _mediator.Send(Arg.Any<ReassignWorkClientCommand>(), Arg.Any<CancellationToken>())
            .Returns((ReassignWorkClientResponse?)null);

        var result = await NewSkill().ExecuteAsync(Ctx(), ByWorkId());

        result.Success.ShouldBeFalse();
    }
}
