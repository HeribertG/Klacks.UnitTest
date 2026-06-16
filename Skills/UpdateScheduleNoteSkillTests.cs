// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateScheduleNoteSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static ScheduleNoteResource Note(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        CurrentDate = new DateOnly(2026, 1, 1),
        Content = "old"
    };

    [Test]
    public async Task UpdateContentAndDate_DispatchesPutCommand()
    {
        var noteId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns(Note(noteId));
        mediator.Send(Arg.Any<PutCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ScheduleNoteResource>)ci[0]).Resource);
        var skill = new UpdateScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["noteId"] = noteId.ToString(),
            ["content"] = "new text",
            ["date"] = "2026-03-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ScheduleNoteResource>>(c =>
                c.Resource.Id == noteId &&
                c.Resource.Content == "new text" &&
                c.Resource.CurrentDate == new DateOnly(2026, 3, 1)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownNote_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns<ScheduleNoteResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["noteId"] = Guid.NewGuid().ToString(),
            ["content"] = "new text"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>());
    }
}
