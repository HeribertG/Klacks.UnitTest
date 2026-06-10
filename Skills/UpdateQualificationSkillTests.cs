// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_qualification: the skill resolves the qualification by id or unambiguous
/// name via ListQuery, merges only the provided multilingual and scalar fields onto the current
/// values and dispatches an UpdateQualificationCommand.
/// </summary>

using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateQualificationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts", "CanEditShifts" }
    };

    private static Qualification Existing(Guid id) => new()
    {
        Id = id,
        Name = new MultiLanguage { De = "Staplerschein" },
        Description = new MultiLanguage { De = "Alt" },
        IsTimeLimited = false,
        Type = QualificationType.Work,
        Category = QualificationCategory.None
    };

    private static IMediator MediatorWith(params Qualification[] qualifications)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(qualifications.ToList().AsEnumerable());
        mediator.Send(Arg.Any<UpdateQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var command = (UpdateQualificationCommand)ci[0];
                return new Qualification { Id = command.Id, Name = command.Name };
            });
        return mediator;
    }

    [Test]
    public async Task UpdateQualification_ById_MergesNameAndCategory()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorWith(Existing(id));
        var skill = new UpdateQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationId"] = id.ToString(),
            ["name_en"] = "Forklift licence",
            ["category"] = "Logistics"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<UpdateQualificationCommand>(c =>
                c.Id == id &&
                c.Name.De == "Staplerschein" &&
                c.Name.En == "Forklift licence" &&
                c.Category == QualificationCategory.Logistics &&
                c.Type == QualificationType.Work &&
                !c.IsTimeLimited),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateQualification_ByName_ResolvesAndDispatches()
    {
        var id = Guid.NewGuid();
        var other = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Erste Hilfe" } };
        var mediator = MediatorWith(Existing(id), other);
        var skill = new UpdateQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationName"] = "Staplerschein",
            ["isTimeLimited"] = true
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<UpdateQualificationCommand>(c => c.Id == id && c.IsTimeLimited),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateQualification_UnknownId_ReturnsError_NoDispatch()
    {
        var mediator = MediatorWith(Existing(Guid.NewGuid()));
        var skill = new UpdateQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationId"] = Guid.NewGuid().ToString(),
            ["name_en"] = "Forklift licence"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<UpdateQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateQualification_InvalidCategory_ReturnsError_NoDispatch()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorWith(Existing(id));
        var skill = new UpdateQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationId"] = id.ToString(),
            ["category"] = "Bogus"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<UpdateQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateQualification_NoFields_ReturnsError_NoDispatch()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorWith(Existing(id));
        var skill = new UpdateQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationId"] = id.ToString()
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<UpdateQualificationCommand>(), Arg.Any<CancellationToken>());
    }
}
