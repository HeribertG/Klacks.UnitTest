// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for BulkDeleteWorksSkill and its confirmation preview: the ids come from the server-side plan (never
/// from the model), a refused plan sends nothing, the handler's all-or-nothing refusals are reported as failures, a
/// partial delete is reported as committed but incomplete with the deleted ids, a failure after the commit is told
/// apart from a refusal by re-reading the stored Works (all gone: deleted with a failed follow-up, some gone: partial,
/// none: nothing deleted), and the preview shows the
/// plan's works at the time of the confirmation - saying that the list is worked out again when the call is confirmed -
/// or refuses before a token is issued.
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

    private static SkillExecutionContext Ctx() => new()
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
    public async Task Preview_SaysHonestlyThatTheListIsWorkedOutAgainWhenTheCallIsConfirmed()
    {
        var provider = new BulkWorkDeletionConfirmationPreviewProvider(_planner);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), Params());

        preview.Text.ShouldContain("worked out again");
        preview.Text.ShouldContain("may differ");
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
