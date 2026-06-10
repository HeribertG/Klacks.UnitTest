// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_schedule_note: the skill dispatches DeleteCommand&lt;ScheduleNoteResource&gt;
/// for the given noteId and reports the deleted note; a null result (unknown note) yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteScheduleNoteSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task DeleteScheduleNote_DispatchesDeleteCommand_AndReportsDeletedNote()
    {
        var noteId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleNoteResource
            {
                Id = noteId,
                ClientId = Guid.NewGuid(),
                CurrentDate = new DateOnly(2026, 6, 15),
                Content = "Obsolete note"
            });
        var skill = new DeleteScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["noteId"] = noteId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<ScheduleNoteResource>>(c => c.Id == noteId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteScheduleNote_UnknownNote_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns((ScheduleNoteResource?)null);
        var skill = new DeleteScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["noteId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }
}
