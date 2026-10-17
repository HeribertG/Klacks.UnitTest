// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ResetContainerDaySkill: a missing override is a benign no-op, an existing override
/// is deleted and its removal is re-verified against the database, and a conflicting delete (work
/// entries already exist for that date) is reported as an error instead of crashing.
/// </summary>

using Klacks.Api.Application.Commands.ContainerShiftOverrides;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Queries.ContainerShiftOverrides;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using NSubstitute.ExceptionExtensions;

using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ResetContainerDaySkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private ResetContainerDaySkill _skill = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid OverrideId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 7, 9);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftVisibilityGuard = ShiftVisibilityGuards.AllowAll();
        _mediator = Substitute.For<IMediator>();
        _skill = new ResetContainerDaySkill(_shiftRepository, _mediator, _shiftVisibilityGuard);

        _shiftRepository.Get(ContainerId).Returns(new Shift
        {
            Id = ContainerId,
            ShiftType = ShiftType.IsContainer
        });
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
        ["containerId"] = ContainerId.ToString(),
        ["date"] = Date.ToString("yyyy-MM-dd")
    };

    [Test]
    public async Task Resets_AndReportsVerified_WhenOverrideIsDeletedAndConfirmedGone()
    {
        _mediator.Send(Arg.Is<GetContainerShiftOverrideQuery>(q => q.ContainerId == ContainerId && q.Date == Date), Arg.Any<CancellationToken>())
            .Returns(
                new ContainerShiftOverrideResource { Id = OverrideId, ContainerId = ContainerId, Date = Date },
                (ContainerShiftOverrideResource?)null);

        _mediator.Send(Arg.Is<DeleteContainerShiftOverrideCommand>(c => c.ContainerId == ContainerId && c.OverrideId == OverrideId), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _mediator.Received(1).Send(Arg.Any<DeleteContainerShiftOverrideCommand>(), Arg.Any<CancellationToken>());
        await _mediator.Received(2).Send(Arg.Any<GetContainerShiftOverrideQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsFriendlySuccess_WithoutDeleting_WhenNoOverrideExists()
    {
        _mediator.Send(Arg.Any<GetContainerShiftOverrideQuery>(), Arg.Any<CancellationToken>())
            .Returns((ContainerShiftOverrideResource?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("nothing to reset"));
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteContainerShiftOverrideCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenDeleteConflictsBecauseWorkAlreadyExists()
    {
        _mediator.Send(Arg.Any<GetContainerShiftOverrideQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerShiftOverrideResource { Id = OverrideId, ContainerId = ContainerId, Date = Date });

        _mediator.Send(Arg.Any<DeleteContainerShiftOverrideCommand>(), Arg.Any<CancellationToken>())
            .Throws(new ConflictException("Cannot delete override: work entries already exist for this date."));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("work entries already exist"));
    }

    [Test]
    public async Task AHiddenContainer_IsAnsweredLikeAMissingOne_AndNothingIsDeleted()
    {
        _skill = new ResetContainerDaySkill(_shiftRepository, _mediator, ShiftVisibilityGuards.Hiding(ContainerId));
        var missingId = Guid.NewGuid();

        var hidden = await _skill.ExecuteAsync(Ctx(), Params());
        var missing = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["containerId"] = missingId.ToString(),
            ["date"] = Date.ToString("yyyy-MM-dd")
        });

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe($"Shift {ContainerId} is not a container shift.");
        missing.Message.ShouldBe($"Shift {missingId} is not a container shift.");
        await _mediator.DidNotReceive().Send(Arg.Any<GetContainerShiftOverrideQuery>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteContainerShiftOverrideCommand>(), Arg.Any<CancellationToken>());
    }
}
