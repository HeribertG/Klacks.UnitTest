// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the thin set_client_qualification / set_shift_required_qualification skills:
/// param + level-range validation and dispatch of the upsert command via IMediator.
/// </summary>

using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetQualificationSkillTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanEditShifts" }
    };

    [Test]
    public async Task SetClientQualification_Valid_DispatchesCommand()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var skill = new SetClientQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["qualificationId"] = QualId.ToString(),
            ["level"] = 3
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<SetClientQualificationCommand>(c =>
                c.ClientId == ClientId && c.QualificationId == QualId && c.Level == QualificationLevel.Proficient),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetClientQualification_InvalidLevel_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetClientQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["qualificationId"] = QualId.ToString(),
            ["level"] = 6
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("level");
        await mediator.DidNotReceive().Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetShiftRequiredQualification_Valid_DispatchesCommand_DefaultMandatory()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var skill = new SetShiftRequiredQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["qualificationId"] = QualId.ToString(),
            ["minLevel"] = 4
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<SetShiftRequiredQualificationCommand>(c =>
                c.ShiftId == ShiftId && c.MinLevel == QualificationLevel.Advanced && c.IsMandatory),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetShiftRequiredQualification_InvalidMinLevel_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetShiftRequiredQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["shiftId"] = ShiftId.ToString(),
            ["qualificationId"] = QualId.ToString(),
            ["minLevel"] = 0
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>());
    }
}
