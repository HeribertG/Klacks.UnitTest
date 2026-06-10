// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_qualification: the skill verifies the qualification exists by resolving
/// it by id or unambiguous name via ListQuery before dispatching a DeleteQualificationCommand,
/// and returns an error without dispatch when the qualification is unknown or ambiguous.
/// </summary>

using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteQualificationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts", "CanEditShifts" }
    };

    private static IMediator MediatorWith(params Qualification[] qualifications)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(qualifications.ToList().AsEnumerable());
        return mediator;
    }

    [Test]
    public async Task DeleteQualification_ById_Dispatches()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorWith(
            new Qualification { Id = id, Name = new MultiLanguage { De = "Staplerschein" } });
        var skill = new DeleteQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteQualificationCommand>(c => c.Id == id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteQualification_ByName_ResolvesAndDispatches()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorWith(
            new Qualification { Id = id, Name = new MultiLanguage { De = "Staplerschein" } },
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Erste Hilfe" } });
        var skill = new DeleteQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationName"] = "Staplerschein"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteQualificationCommand>(c => c.Id == id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteQualification_UnknownId_ReturnsError_NoDispatch()
    {
        var mediator = MediatorWith(
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Staplerschein" } });
        var skill = new DeleteQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<DeleteQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteQualification_AmbiguousName_ReturnsError_NoDispatch()
    {
        var mediator = MediatorWith(
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Pflege Basis" } },
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Pflege Plus" } });
        var skill = new DeleteQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["qualificationName"] = "Pflege"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<DeleteQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteQualification_MissingIdentifier_ReturnsError_NoDispatch()
    {
        var mediator = MediatorWith();
        var skill = new DeleteQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<DeleteQualificationCommand>(), Arg.Any<CancellationToken>());
    }
}
