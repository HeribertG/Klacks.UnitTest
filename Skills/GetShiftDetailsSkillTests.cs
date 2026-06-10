// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_shift_details: the skill dispatches GetQuery&lt;ShiftResource&gt; for the
/// given shiftId and projects the full shift definition; a missing shift (null result or
/// KeyNotFoundException from the handler) yields an error result.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetShiftDetailsSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task GetShiftDetails_DispatchesGetQuery_AndProjectsDetails()
    {
        var shiftId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ShiftResource
            {
                Id = shiftId,
                Name = "Night shift",
                Abbreviation = "NS",
                FromDate = new DateOnly(2026, 1, 1),
                StartShift = new TimeOnly(22, 0),
                EndShift = new TimeOnly(6, 0),
                IsMonday = true,
                IsTuesday = true,
                Quantity = 2
            });
        var skill = new GetShiftDetailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = shiftId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetQuery<ShiftResource>>(q => q.Id == shiftId),
            Arg.Any<CancellationToken>());

        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Name").GetString().ShouldBe("Night shift");
        data.GetProperty("Quantity").GetInt32().ShouldBe(2);
        data.GetProperty("Weekdays").GetProperty("IsMonday").GetBoolean().ShouldBeTrue();
        data.GetProperty("Weekdays").GetProperty("IsSunday").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task GetShiftDetails_NullResult_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns((ShiftResource?)null);
        var skill = new GetShiftDetailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task GetShiftDetails_HandlerThrowsKeyNotFound_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("Shift not found"));
        var skill = new GetShiftDetailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }
}
