// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_schedule_notes: the skill dispatches ListQuery&lt;ScheduleNoteResource&gt;,
/// optionally narrows the result to one client and orders by date; an invalid clientId aborts
/// without dispatch.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListScheduleNotesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task ListScheduleNotes_FiltersByClient_AndOrdersByDate()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleNoteResource>
            {
                new() { Id = Guid.NewGuid(), ClientId = clientA, CurrentDate = new DateOnly(2026, 6, 20), Content = "Later note" },
                new() { Id = Guid.NewGuid(), ClientId = clientB, CurrentDate = new DateOnly(2026, 6, 11), Content = "Other client" },
                new() { Id = Guid.NewGuid(), ClientId = clientA, CurrentDate = new DateOnly(2026, 6, 10), Content = "Earlier note" }
            }.AsEnumerable());
        var skill = new ListScheduleNotesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientA.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Any<ListQuery<ScheduleNoteResource>>(), Arg.Any<CancellationToken>());

        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
        data.GetProperty("Notes")[0].GetProperty("Content").GetString().ShouldBe("Earlier note");
        data.GetProperty("Notes")[1].GetProperty("Content").GetString().ShouldBe("Later note");
    }

    [Test]
    public async Task ListScheduleNotes_WithoutClientId_ReturnsAllNotes()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<ScheduleNoteResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleNoteResource>
            {
                new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 6, 10), Content = "A" },
                new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 6, 11), Content = "B" }
            }.AsEnumerable());
        var skill = new ListScheduleNotesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task ListScheduleNotes_InvalidClientId_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ListScheduleNotesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = "not-a-guid"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(
            Arg.Any<ListQuery<ScheduleNoteResource>>(), Arg.Any<CancellationToken>());
    }
}
