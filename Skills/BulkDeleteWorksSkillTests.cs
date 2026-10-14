// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for BulkDeleteWorksSkill and its confirmation preview: the ids come from the server-side plan (never
/// from the model), a refused plan sends nothing, the handler's all-or-nothing refusals are reported as failures, a
/// partial delete is reported as committed but incomplete with the deleted ids, a failure after the commit is told
/// apart from a refusal by re-reading the stored Works (all gone: deleted with a failed follow-up, some gone: partial,
/// none: nothing deleted), and the preview shows the
/// plan's works at the time of the confirmation, binds exactly those works to the confirmation (work ids, "N" format)
/// and says so, or refuses before a token is issued. The confirmed call runs only with the binding of its own preview
/// in the context (none, or one of another kind: refused before anything is planned, also on a background path) and only
/// when the plan it works out now has exactly the bound works and container entries; any difference (added, gone, swapped,
/// a container entry that appeared or vanished) deletes nothing and reports the counts, or only the scope text when parents
/// and entries together exceed the storage cap of a binding. The preview names the number of container entries that go
/// with the listed works and binds them. The command carries the container children of the bound plan as ExpectedChildren
/// (never null, empty for a plan without children), and a handler refusal for a child that appeared after the plan is
/// reported as nothing deleted.
/// </summary>

using FluentValidation;
using FluentValidation.Results;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BulkDeleteWorksSkillTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly Until = new(2026, 11, 30);

    private IMediator _mediator = null!;
    private IBulkWorkDeletionPlanner _planner = null!;
    private IWorkRepository _workRepository = null!;
    private List<PlannedWorkDeletion> _works = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _planner = Substitute.For<IBulkWorkDeletionPlanner>();
        _workRepository = Substitute.For<IWorkRepository>();
        _works =
        [
            new(Guid.NewGuid(), new DateOnly(2026, 11, 2), Guid.NewGuid(), "Early", new TimeOnly(6, 0), new TimeOnly(14, 0)),
            new(Guid.NewGuid(), new DateOnly(2026, 11, 3), Guid.NewGuid(), "Late", new TimeOnly(14, 0), new TimeOnly(22, 0))
        ];
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorkDeletionPlan(null, "Max Müller", From, Until, _works));
        GivenStillActive(_works.Select(w => w.WorkId).ToArray());
    }

    private BulkDeleteWorksSkill NewSkill() => new(_mediator, _planner, _workRepository);

    private void GivenStillActive(params Guid[] workIds)
    {
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(workIds.Select(id => new Work { Id = id }).ToList());
    }

    private static ConfirmationBinding BindingOf(IEnumerable<Guid> workIds) =>
        ConfirmationBinding.Of(ConfirmationBindingKinds.Work, workIds.Select(id => id.ToString("N")));

    private SkillExecutionContext Ctx() => CtxBoundTo(_works.Select(w => w.WorkId));

    private static SkillExecutionContext CtxBoundTo(IEnumerable<Guid> workIds) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = "de",
        UserPermissions = new List<string> { "CanEditSchedule" },
        ConfirmedActionBinding = BindingOf(workIds)
    };

    private static SkillExecutionContext CtxWithoutBinding() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = "de",
        UserPermissions = new List<string> { "CanEditSchedule" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["firstName"] = "Max",
        ["lastName"] = "Müller",
        ["fromDate"] = "2026-11-01",
        ["untilDate"] = "2026-11-30"
    };

    private BulkWorksResponse GivenHandlerDeletes(int success, int failed)
    {
        var response = new BulkWorksResponse
        {
            SuccessCount = success,
            FailedCount = failed,
            DeletedIds = _works.Take(success).Select(w => w.WorkId).ToList()
        };
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>()).Returns(response);
        return response;
    }

    [Test]
    public async Task Deletes_ExactlyTheIdsOfTheServerPlan_ThroughTheBulkDeleteCommand()
    {
        GivenHandlerDeletes(2, 0);
        var parameters = Params();
        parameters["workIds"] = Guid.NewGuid().ToString();

        var result = await NewSkill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<BulkDeleteWorksCommand>(c => c.Request.WorkIds.SequenceEqual(_works.Select(w => w.WorkId))),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("2");
        result.Message.ShouldContain("Max Müller");
    }

    [Test]
    public async Task WithoutABinding_RefusesAndDeletesNothing_EvenOnABypassedPath()
    {
        GivenHandlerDeletes(2, 0);
        var context = CtxWithoutBinding() with { BypassAutonomyGate = true };

        var result = await NewSkill().ExecuteAsync(context, Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, BulkDeleteWorksSkill.SkillName));
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default);
    }

    [Test]
    public async Task WithoutABinding_AndARefusablePlan_ReportsTheMissingBinding_NotThePlannersRefusal()
    {
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BulkWorkDeletionPlan.Refuse("A work is locked."));

        var result = await NewSkill().ExecuteAsync(CtxWithoutBinding(), Params());

        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, BulkDeleteWorksSkill.SkillName));
        result.Message.ShouldNotContain("locked");
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default);
    }

    [Test]
    public async Task ABindingOfAnotherKind_IsTreatedAsNoBinding()
    {
        GivenHandlerDeletes(2, 0);
        var context = CtxWithoutBinding() with
        {
            ConfirmedActionBinding = ConfirmationBinding.Of(
                ConfirmationBindingKinds.MacroSwitch, _works.Select(w => w.WorkId.ToString("N")))
        };

        var result = await NewSkill().ExecuteAsync(context, Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("only runs as the confirmed replay");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default);
    }

    [Test]
    public async Task ABindingProvidedAsAParameter_IsNeverRead()
    {
        GivenHandlerDeletes(2, 0);
        var parameters = Params();
        parameters["confirmedActionBinding"] = BindingOf(_works.Select(w => w.WorkId));

        var result = await NewSkill().ExecuteAsync(CtxWithoutBinding(), parameters);

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWorkAddedAfterThePreview_DeletesNothing_AndReportsTheCounts()
    {
        var shown = _works.Select(w => w.WorkId).ToList();
        _works.Add(new(Guid.NewGuid(), new DateOnly(2026, 11, 4), Guid.NewGuid(), "Night", new TimeOnly(22, 0), new TimeOnly(6, 0)));

        var result = await NewSkill().ExecuteAsync(CtxBoundTo(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        result.Message.ShouldContain("Nothing was changed");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWorkGoneAfterThePreview_DeletesNothing_AndReportsTheCounts()
    {
        var shown = _works.Select(w => w.WorkId).ToList();
        _works.RemoveAt(1);

        var result = await NewSkill().ExecuteAsync(CtxBoundTo(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 1 apply now (0 new, 1 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASwappedWork_DeletesNothing_AndReportsOneNewAndOneGone()
    {
        var shown = _works.Select(w => w.WorkId).ToList();
        _works[1] = new(Guid.NewGuid(), new DateOnly(2026, 11, 3), Guid.NewGuid(), "Late", new TimeOnly(14, 0), new TimeOnly(22, 0));

        var result = await NewSkill().ExecuteAsync(CtxBoundTo(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("(1 new, 1 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ABindingWithoutItems_RefusesWithTheScopeTextWhenThePlanDiffers()
    {
        var shown = Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems + 1).Select(_ => Guid.NewGuid()).ToList();

        var result = await NewSkill().ExecuteAsync(CtxBoundTo(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ConfirmationBindingMessages.ScopeChanged);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheOrderOfThePlannedWorks_DoesNotMatter()
    {
        GivenHandlerDeletes(2, 0);
        var shown = _works.Select(w => w.WorkId).Reverse().ToList();

        var result = await NewSkill().ExecuteAsync(CtxBoundTo(shown), Params());

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task ARefusedPlanOfTheReplay_IsReportedAsTheRefusal()
    {
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BulkWorkDeletionPlan.Refuse("A work is locked."));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("A work is locked.");
    }

    [Test]
    public async Task PlansWithTheCallersLanguage()
    {
        GivenHandlerDeletes(2, 0);

        await NewSkill().ExecuteAsync(Ctx(), Params());

        await _planner.Received(1).PlanAsync(Arg.Any<Dictionary<string, object>>(), "de", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefusedPlan_SendsNothing_AndReportsTheReason()
    {
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BulkWorkDeletionPlan.Refuse("Too many works."));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Too many works.");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnyFailedId_IsReportedAsAPartialDelete_NeverAsComplete()
    {
        GivenHandlerDeletes(1, 1);

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 of 2");
        result.Message.ShouldContain("not all");
        result.Message.ShouldNotContain("nothing was deleted");
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"Partial\":true");
        json.ShouldContain(_works[0].WorkId.ToString());
        json.ShouldNotContain(_works[1].WorkId.ToString());
    }

    [Test]
    public async Task HandlerDeletedNothing_IsReportedAsAFailure_NothingDeleted()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { SuccessCount = 0, FailedCount = 2 });

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was deleted");
    }

    [Test]
    public async Task SealedWorkValidation_IsReportedAsNothingDeleted()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new ValidationException(
                [new ValidationFailure("WorkIds", "Cannot delete sealed work entries.")]));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was deleted");
        result.Message.ShouldContain("Cannot delete sealed work entries.");
    }

    [Test]
    public async Task LockOrDayLockRefusalOfTheHandler_IsReportedAsNothingDeleted()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new InvalidRequestException("Day is sealed."));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was deleted");
        result.Message.ShouldContain("Day is sealed.");
    }

    [Test]
    public async Task HandlerFailedAfterTheCommit_AllWorksGone_ReportsThemDeletedAndTheFollowUpFailed()
    {
        GivenStillActive();
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new InvalidRequestException(
                "An unexpected error occurred during BulkDeleteWorks: period hours failed"));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WERE deleted");
        result.Message.ShouldContain("follow-up");
        result.Message.ShouldContain("period hours failed");
        result.Message.ShouldNotContain("nothing was deleted");
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("FollowUpFailed");
        foreach (var work in _works)
        {
            json.ShouldContain(work.WorkId.ToString());
        }
    }

    [Test]
    public async Task HandlerFailedAfterTheCommit_OnlySomeWorksGone_ReportsAPartialDeleteWithTheDeletedIds()
    {
        GivenStillActive(_works[1].WorkId);
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new InvalidRequestException("boom"));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 of 2");
        result.Message.ShouldContain("not all");
        result.Message.ShouldContain("boom");
        result.Message.ShouldNotContain("nothing was deleted");
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"Partial\":true");
        json.ShouldContain(_works[0].WorkId.ToString());
        json.ShouldNotContain(_works[1].WorkId.ToString());
    }

    [Test]
    public async Task KeyNotFoundAfterTheCommit_AllWorksGone_IsReportedAsDeleted()
    {
        GivenStillActive();
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new KeyNotFoundException("missing period"));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WERE deleted");
        result.Message.ShouldContain("missing period");
    }

    [Test]
    public async Task KeyNotFoundBeforeTheCommit_IsReportedAsNothingDeleted()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new KeyNotFoundException("Work not found."));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was deleted");
    }

    [Test]
    public async Task ConflictAfterTheCommit_AllWorksGone_IsAlsoReportedAsDeleted()
    {
        GivenStillActive();
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new ConflictException("late"));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WERE deleted");
    }

    [Test]
    public async Task ValidationRefusal_NeverReadsTheStoredWorks()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new ValidationException(
                [new ValidationFailure("WorkIds", "Cannot delete sealed work entries.")]));

        await NewSkill().ExecuteAsync(Ctx(), Params());

        await _workRepository.DidNotReceiveWithAnyArgs().GetByIdsAsync(Arg.Any<IEnumerable<Guid>>());
    }

    [Test]
    public async Task ConflictRefusalOfTheHandler_IsReportedAsNothingDeleted()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new ConflictException("Blocked."));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was deleted");
    }

    [Test]
    public async Task Preview_ListsEveryPlannedWork_AndTheCountAndTheRange()
    {
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.IsRefusal.ShouldBeFalse();
        preview.Text.ShouldContain("Max Müller");
        preview.Text.ShouldContain("2026-11-01");
        preview.Text.ShouldContain("2026-11-30");
        preview.Text.ShouldContain("2026-11-02");
        preview.Text.ShouldContain("Early");
        preview.Text.ShouldContain("06:00-14:00");
        preview.Text.ShouldContain("2026-11-03");
        preview.Text.ShouldContain("Late");
        preview.Text.ShouldContain("2 work");
        preview.Text.ShouldContain(WorkRestoreDefaults.UndoWindowSeconds.ToString());
    }

    [Test]
    public async Task Preview_SaysThatExactlyTheListedWorksAreBoundToTheConfirmation()
    {
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.Text.ShouldContain("Exactly these works and their container entries are bound to the confirmation");
        preview.Text.ShouldContain("nothing is deleted");
        preview.Text.ShouldNotContain("worked out again");
        preview.Text.ShouldNotContain("may differ");
    }

    private static BulkWorkDeletionPlan PlanWith(
        IReadOnlyList<PlannedWorkDeletion> works, IReadOnlyList<Guid> childWorkIds, IReadOnlyList<Guid> childBreakIds) =>
        new(null, "Max Müller", From, Until, works) { ChildWorkIds = childWorkIds, ChildBreakIds = childBreakIds };

    private void GivenPlan(BulkWorkDeletionPlan plan) =>
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

    private static SkillExecutionContext CtxBoundToPlan(BulkWorkDeletionPlan plan) => CtxWithoutBinding() with
    {
        ConfirmedActionBinding = BulkWorkDeletionBinding.Of(plan)
    };

    private static List<PlannedWorkDeletion> ManyWorks(int count) => Enumerable.Range(0, count)
        .Select(i => new PlannedWorkDeletion(
            Guid.NewGuid(), From.AddDays(i % 20), Guid.NewGuid(), "Early", new TimeOnly(6, 0), new TimeOnly(14, 0)))
        .ToList();

    [Test]
    public async Task AContainerChildThatAppearedAfterThePreview_DeletesNothing_AndReportsTheCounts()
    {
        var shown = PlanWith(_works, [Guid.NewGuid()], []);
        GivenPlan(PlanWith(_works, [shown.ChildWorkIds[0], Guid.NewGuid()], []));

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("3 item(s) were shown, 4 apply now (1 new, 0 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContainerBreakThatAppearedAfterThePreview_DeletesNothing()
    {
        var shown = PlanWith(_works, [], []);
        GivenPlan(PlanWith(_works, [], [Guid.NewGuid()]));

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContainerChildGoneAfterThePreview_DeletesNothing()
    {
        var shown = PlanWith(_works, [Guid.NewGuid()], [Guid.NewGuid()]);
        GivenPlan(PlanWith(_works, [], [shown.ChildBreakIds[0]]));

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("4 item(s) were shown, 3 apply now (0 new, 1 no longer there)");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheSameContainerChildren_AreStillTheConfirmedScope_AndTheWorksAreDeleted()
    {
        GivenHandlerDeletes(2, 0);
        var plan = PlanWith(_works, [Guid.NewGuid()], [Guid.NewGuid()]);
        GivenPlan(plan);

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(plan), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<BulkDeleteWorksCommand>(c => c.Request.WorkIds.SequenceEqual(_works.Select(w => w.WorkId))),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheCommandCarriesTheContainerChildrenOfTheBoundPlan_SoTheHandlerCanCloseTheMillisecondWindow()
    {
        GivenHandlerDeletes(2, 0);
        var plan = PlanWith(_works, [Guid.NewGuid(), Guid.NewGuid()], [Guid.NewGuid()]);
        GivenPlan(plan);

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(plan), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<BulkDeleteWorksCommand>(c => c.ExpectedChildren != null
                && c.ExpectedChildren.HasSameEntriesAs(new ContainerChildIds(plan.ChildWorkIds, plan.ChildBreakIds))),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task APlanWithoutContainerChildren_StillSendsAnEmptyExpectation_NeverNull()
    {
        GivenHandlerDeletes(2, 0);

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<BulkDeleteWorksCommand>(c => c.ExpectedChildren != null && c.ExpectedChildren.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContainerChildThatAppearedBetweenThePlanAndTheDelete_IsReportedAsNothingDeleted()
    {
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns<BulkWorksResponse>(_ => throw new InvalidRequestException(
                string.Format(ConfirmationBindingMessages.ContainerChildrenChanged, 2, 3)));

        var result = await NewSkill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("nothing was deleted");
        result.Message.ShouldContain("fresh preview");
        result.Message.ShouldNotContain("WERE deleted");
    }

    [Test]
    public async Task ParentsAndChildrenBeyondTheStorageCap_KeepOnlyTheFingerprint_AndRefuseWithTheScopeText()
    {
        var parents = ManyWorks(30);
        var children = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        var shown = PlanWith(parents, children, []);
        BulkWorkDeletionBinding.Of(shown).Items.ShouldBeEmpty("30 parents + 30 children exceed the storage cap");
        GivenPlan(PlanWith(parents, children.Skip(1).Append(Guid.NewGuid()).ToList(), []));

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(shown), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ConfirmationBindingMessages.ScopeChanged);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ParentsAndChildrenBeyondTheStorageCap_StillRunWhenNothingChanged()
    {
        var parents = ManyWorks(30);
        var plan = PlanWith(parents, Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList(), []);
        GivenPlan(plan);
        _mediator.Send(Arg.Any<BulkDeleteWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { SuccessCount = 30, DeletedIds = parents.Select(w => w.WorkId).ToList() });

        var result = await NewSkill().ExecuteAsync(CtxBoundToPlan(plan), Params());

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task Preview_NamesTheNumberOfContainerEntriesThatGoWithTheWorks_AndBindsThem()
    {
        var plan = PlanWith(_works, [Guid.NewGuid(), Guid.NewGuid()], [Guid.NewGuid()]);
        GivenPlan(plan);
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.Text.ShouldContain("together with 3 container entries");
        preview.Text.ShouldContain("Delete 2 work(s)");
        preview.Binding.ShouldNotBeNull();
        preview.Binding.Matches(BulkWorkDeletionBinding.Of(plan)).ShouldBeTrue();
        preview.Binding.Matches(BindingOf(_works.Select(w => w.WorkId))).ShouldBeFalse();
    }

    [Test]
    public async Task Preview_WithoutContainerEntries_DoesNotMentionThem()
    {
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.Text.ShouldNotContain("together with");
    }

    [Test]
    public async Task Preview_BindsEveryPlannedWorkIdInNFormat()
    {
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.Binding.ShouldNotBeNull();
        preview.Binding.Kind.ShouldBe(ConfirmationBindingKinds.Work);
        preview.Binding.Items.ShouldBe(_works.Select(w => w.WorkId.ToString("N")).OrderBy(id => id, StringComparer.Ordinal));
        preview.Binding.Matches(BindingOf(_works.Select(w => w.WorkId))).ShouldBeTrue();
    }

    [Test]
    public async Task Preview_OfARefusedPlan_CarriesNoBinding()
    {
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BulkWorkDeletionPlan.Refuse("Two days are sealed."));
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.Binding.ShouldBeNull();
    }

    [Test]
    public async Task Preview_OfARefusedPlan_RefusesSoNoTokenIsIssued()
    {
        _planner.PlanAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BulkWorkDeletionPlan.Refuse("Two days are sealed."));
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldContain("Two days are sealed.");
    }

    [Test]
    public void Preview_SupportsOnlyTheBulkDeleteSkill()
    {
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        provider.Supports(BulkDeleteWorksSkill.SkillName).ShouldBeTrue();
        provider.Supports("delete_work").ShouldBeFalse();
    }
}
