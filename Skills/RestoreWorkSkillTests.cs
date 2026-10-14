// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for RestoreWorkSkill: a restore goes through RestoreWorkCommand only, an expired undo window
/// is reported as expired (never as a generic failure), a work the caller cannot restore reads as not found,
/// and a guard refusal is reported as "not restored, nothing changed" with its reason - unless the stored Work is active
/// again, which means the restore was committed and only a follow-up step failed.
/// </summary>

using FluentValidation;
using FluentValidation.Results;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Validation.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class RestoreWorkSkillTests
{
    private static readonly Guid WorkId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IWorkRepository _workRepository = null!;
    private RestoreWorkSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.GetNoTracking(WorkId).Returns((Work?)null);
        _workRepository.GetDeletedAsync(WorkId, Arg.Any<CancellationToken>()).Returns(new Work { Id = WorkId });
        _skill = new RestoreWorkSkill(_mediator, _workRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSchedule" }
    };

    private static Dictionary<string, object> Params() => new() { ["workId"] = WorkId.ToString() };

    private static ValidationException Rejected(string message)
        => new([new ValidationFailure("Id", message)]);

    [Test]
    public async Task Restores_ThroughTheRestoreCommand_AndReportsTheWork()
    {
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns(new WorkResource
            {
                Id = WorkId,
                ClientId = clientId,
                ShiftId = shiftId,
                CurrentDate = new DateOnly(2026, 10, 14)
            });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2026-10-14");
        await _mediator.Received(1).Send(
            Arg.Is<RestoreWorkCommand>(c => c.Id == WorkId), Arg.Any<CancellationToken>());
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain(WorkId.ToString());
        json.ShouldContain(clientId.ToString());
    }

    [Test]
    public async Task ExpiredUndoWindow_IsReportedAsExpired_WithTheWindowLength()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw Rejected(RestoreWorkCommandValidator.UndoWindowExpiredMessage));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("expired");
        result.Message.ShouldContain(WorkRestoreDefaults.UndoWindowSeconds.ToString());
        result.Message.ShouldContain("administrator");
    }

    [Test]
    public async Task SealedWork_IsReportedAsSealed_NotAsExpired()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw Rejected(RestoreWorkCommandValidator.SealedWorkMessage));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("sealed");
        result.Message.ShouldNotContain("expired");
    }

    [Test]
    public async Task UnknownOrForeignOrHiddenWork_ReadsAsNotFound_WithoutHintingWhichOne()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new KeyNotFoundException("Deleted work with ID x not found."));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldContain("Nothing was changed");
    }

    [Test]
    public async Task OtherValidationFailure_IsReportedWithItsMessage()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw Rejected("Something else is wrong."));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Something else is wrong.");
    }

    [Test]
    public async Task GuardRefusal_IsReportedAsNotRestored_WithTheReason()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new InvalidRequestException("Day 2026-10-14 is sealed."));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not restored");
        result.Message.ShouldContain("Day 2026-10-14 is sealed.");
        result.Message.ShouldContain("Nothing was changed");
    }

    [Test]
    public async Task HandlerFailedAfterTheCommit_WorkIsActiveAgain_ReportsItWasRestoredAndTheFollowUpFailed()
    {
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _workRepository.GetNoTracking(WorkId).Returns(new Work
        {
            Id = WorkId,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = new DateOnly(2026, 10, 14)
        });
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new InvalidRequestException(
                "An unexpected error occurred during restoring work: period hours failed"));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WAS restored");
        result.Message.ShouldContain("follow-up");
        result.Message.ShouldContain("period hours failed");
        result.Message.ShouldNotContain("not restored");
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("FollowUpFailed");
        json.ShouldContain(WorkId.ToString());
        json.ShouldContain(clientId.ToString());
    }

    [Test]
    public async Task NotFoundAfterTheCommit_WorkWasDeletedBeforeAndIsActiveNow_ReportsItWasRestored()
    {
        _workRepository.GetNoTracking(WorkId).Returns(new Work { Id = WorkId, CurrentDate = new DateOnly(2026, 10, 14) });
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new KeyNotFoundException("lookup in the recalculation"));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WAS restored");
    }

    [Test]
    public async Task NotFound_ForAWorkThatWasNeverDeleted_StaysNotFound_EvenThoughItIsActive()
    {
        _workRepository.GetDeletedAsync(WorkId, Arg.Any<CancellationToken>()).Returns((Work?)null);
        _workRepository.GetNoTracking(WorkId).Returns(new Work { Id = WorkId, CurrentDate = new DateOnly(2026, 10, 14) });
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new KeyNotFoundException("Deleted work with ID x not found."));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldContain("Nothing was changed");
        result.Message.ShouldNotContain("WAS restored");
    }

    [Test]
    public async Task ConflictAfterTheCommit_WorkIsActiveAgain_AlsoReportsItWasRestored()
    {
        _workRepository.GetNoTracking(WorkId).Returns(new Work { Id = WorkId, CurrentDate = new DateOnly(2026, 10, 14) });
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new ConflictException("late conflict"));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("WAS restored");
    }

    [Test]
    public async Task SlotTakenConflict_IsReportedAsNotRestored_WithTheReason()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new ConflictException("Restore blocked: the slot is taken."));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not restored");
        result.Message.ShouldContain("the slot is taken");
        result.Message.ShouldContain("Nothing was changed");
    }

    [Test]
    public async Task EmptyHandlerResult_IsAnError_NeverASuccess()
    {
        _mediator.Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns((WorkResource?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task MissingWorkId_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));
        await _mediator.DidNotReceive().Send(Arg.Any<RestoreWorkCommand>(), Arg.Any<CancellationToken>());
    }
}
