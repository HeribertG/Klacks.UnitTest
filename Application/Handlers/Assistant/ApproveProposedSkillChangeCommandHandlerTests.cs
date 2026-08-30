// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ApproveProposedSkillChangeCommandHandler - apply, stale-skip, missing-proposal/skill paths.
/// Since the learning loop applies pending proposals by itself, this handler is the administrator's
/// override of the routing regression gate: it accepts a regression-blocked proposal and refuses a pending
/// one, which belongs to the loop.
/// The outcome is a LearningMutationResult like every other mutation on the learning card: not found is
/// 404, the stale-description auto-reject is 409, everything else refusing is 400.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ApproveProposedSkillChangeCommandHandlerTests
{
    private IProposedSkillChangeRepository _proposalRepo = null!;
    private IAgentSkillRepository _skillRepo = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private ILogger<ApproveProposedSkillChangeCommandHandler> _logger = null!;
    private ApproveProposedSkillChangeCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _proposalRepo = Substitute.For<IProposedSkillChangeRepository>();
        _skillRepo = Substitute.For<IAgentSkillRepository>();
        _refresher = Substitute.For<ISkillCatalogRefresher>();
        _logger = Substitute.For<ILogger<ApproveProposedSkillChangeCommandHandler>>();

        _handler = new ApproveProposedSkillChangeCommandHandler(
            _proposalRepo, _skillRepo, _refresher, _logger);
    }

    [Test]
    public async Task Handle_HappyPath_AppliesAndReloads()
    {
        var skillId = Guid.NewGuid();
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = skillId,
            SkillName = "delete_employee",
            Field = ProposedChangeFields.Description,
            ValueBefore = "Old description",
            ValueAfter = "Tighter description",
            Status = ProposedChangeStatuses.BlockedRegression
        };
        var skill = new AgentSkill { Id = skillId, Name = "delete_employee", Description = "Old description", Version = 3 };

        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _skillRepo.GetByIdAsync(skillId, Arg.Any<CancellationToken>()).Returns(skill);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin@klacks"
        }, CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Conflict.ShouldBeFalse();
        result.Error.ShouldBeNull();
        skill.Description.ShouldBe("Tighter description");
        skill.Version.ShouldBe(4);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Approved);
        proposal.ReviewedBy.ShouldBe("admin@klacks");

        await _skillRepo.Received(1).UpdateAsync(skill, Arg.Any<CancellationToken>());
        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_StaleProposal_AutoRejectsAsConflict()
    {
        var skillId = Guid.NewGuid();
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = skillId,
            Field = ProposedChangeFields.Description,
            ValueBefore = "Old description",
            ValueAfter = "Tighter description",
            Status = ProposedChangeStatuses.BlockedRegression
        };
        var skill = new AgentSkill { Id = skillId, Description = "Description was changed elsewhere", Version = 5 };

        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _skillRepo.GetByIdAsync(skillId, Arg.Any<CancellationToken>()).Returns(skill);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Conflict.ShouldBeTrue();
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        skill.Description.ShouldBe("Description was changed elsewhere");
        await _skillRepo.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _refresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Two writers for one transition is the failure mode this guards: the loop applies pending proposals
    // automatically, so a person approving one in parallel would race it and the loser would be silent.
    [Test]
    public async Task Handle_PendingProposal_IsLeftToTheLearningLoop()
    {
        var proposal = new ProposedSkillChange { Id = Guid.NewGuid(), Status = ProposedChangeStatuses.Pending };
        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Error.ShouldNotBeNull().ShouldContain("pending");
        await _skillRepo.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AlreadyApprovedProposal_ReturnsError()
    {
        var proposal = new ProposedSkillChange { Id = Guid.NewGuid(), Status = ProposedChangeStatuses.Approved };
        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Error.ShouldNotBeNull().ShouldContain("approved");
    }

    [Test]
    public async Task Handle_MissingProposal_AnswersNotFound()
    {
        _proposalRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ProposedSkillChange?)null);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = Guid.NewGuid(),
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Found.ShouldBeFalse();
        result.Error.ShouldBeNull();
    }

    [Test]
    public async Task Handle_MissingSkill_AnswersNotFound()
    {
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = Guid.NewGuid(),
            Field = ProposedChangeFields.Description,
            Status = ProposedChangeStatuses.BlockedRegression
        };
        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _skillRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentSkill?)null);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Found.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_MissingReviewedBy_ReturnsError()
    {
        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = Guid.NewGuid(),
            ReviewedBy = string.Empty
        }, CancellationToken.None);

        result.Error.ShouldNotBeNull().ShouldContain("ReviewedBy");
    }
}
