// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for add_schedule_note: the skill builds a ScheduleNoteResource from clientId,
/// date and content and dispatches PostCommand&lt;ScheduleNoteResource&gt;; missing content or
/// date aborts without dispatch.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddScheduleNoteSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task AddScheduleNote_DispatchesPostCommand_WithClientDateAndContent()
    {
        var clientId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PostCommand<ScheduleNoteResource>)ci[0]).Resource);
        var skill = new AddScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientId.ToString(),
            ["date"] = new DateOnly(2026, 6, 15),
            ["content"] = "Prefers early shifts this week"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ScheduleNoteResource>>(c =>
                c.Resource.ClientId == clientId &&
                c.Resource.CurrentDate == new DateOnly(2026, 6, 15) &&
                c.Resource.Content == "Prefers early shifts this week" &&
                c.Resource.AnalyseToken == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddScheduleNote_MissingContent_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new AddScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["date"] = new DateOnly(2026, 6, 15)
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(
            Arg.Any<PostCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddScheduleNote_MissingDate_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new AddScheduleNoteSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["content"] = "No date given"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(
            Arg.Any<PostCommand<ScheduleNoteResource>>(), Arg.Any<CancellationToken>());
    }
}
