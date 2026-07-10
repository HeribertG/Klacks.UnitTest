// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RemoveShiftRequiredQualificationSkill: an existing requirement is deleted via
/// DeleteShiftRequiredQualificationCommand, and a shift without the requested requirement is rejected
/// with the currently required qualification ids listed, without sending any command.
/// </summary>

using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class RemoveShiftRequiredQualificationSkillTests
{
    private IShiftRequiredQualificationRepository _repository = null!;
    private IMediator _mediator = null!;
    private RemoveShiftRequiredQualificationSkill _skill = null!;

    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualificationId = Guid.NewGuid();
    private static readonly Guid RequirementId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IShiftRequiredQualificationRepository>();
        _mediator = Substitute.For<IMediator>();
        _skill = new RemoveShiftRequiredQualificationSkill(_repository, _mediator);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["qualificationId"] = QualificationId.ToString()
    };

    [Test]
    public async Task Removes_WhenRequirementExists()
    {
        _repository.GetActiveAsync(ShiftId, QualificationId, Arg.Any<CancellationToken>())
            .Returns(new ShiftRequiredQualification
            {
                Id = RequirementId,
                ShiftId = ShiftId,
                QualificationId = QualificationId,
                MinLevel = QualificationLevel.Basic,
                IsMandatory = true
            });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<DeleteShiftRequiredQualificationCommand>(c => c.Id == RequirementId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_AndListsCurrentRequirements_WhenRequirementDoesNotExist()
    {
        var otherQualificationId = Guid.NewGuid();
        _repository.GetActiveAsync(ShiftId, QualificationId, Arg.Any<CancellationToken>())
            .Returns((ShiftRequiredQualification?)null);
        _repository.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<ShiftRequiredQualification>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ShiftId = ShiftId,
                    QualificationId = otherQualificationId,
                    MinLevel = QualificationLevel.Advanced,
                    IsMandatory = false
                }
            });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain(otherQualificationId.ToString()));
        await _mediator.DidNotReceive().Send(
            Arg.Any<DeleteShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenShiftHasNoRequirementsAtAll()
    {
        _repository.GetActiveAsync(ShiftId, QualificationId, Arg.Any<CancellationToken>())
            .Returns((ShiftRequiredQualification?)null);
        _repository.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<ShiftRequiredQualification>());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("no required qualifications at all"));
        await _mediator.DidNotReceive().Send(
            Arg.Any<DeleteShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>());
    }
}
