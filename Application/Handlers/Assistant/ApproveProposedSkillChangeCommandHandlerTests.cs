// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ApproveProposedSkillChangeCommandHandler — apply, stale-skip, missing-proposal/skill paths.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ApproveProposedSkillChangeCommandHandlerTests
{
    private IProposedSkillChangeRepository _proposalRepo = null!;
    private IAgentSkillRepository _skillRepo = null!;
    private ISkillCacheService _cache = null!;
    private SkillRegistryInitializer _initializer = null!;
    private IKnowledgeIndexSynchronizer _knowledgeSync = null!;
    private ILogger<ApproveProposedSkillChangeCommandHandler> _logger = null!;
    private ApproveProposedSkillChangeCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _proposalRepo = Substitute.For<IProposedSkillChangeRepository>();
        _skillRepo = Substitute.For<IAgentSkillRepository>();
        _cache = Substitute.For<ISkillCacheService>();
        _initializer = Substitute.For<SkillRegistryInitializer>(
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<ISkillRegistry>(),
            Substitute.For<ILogger<SkillRegistryInitializer>>());
        _knowledgeSync = Substitute.For<IKnowledgeIndexSynchronizer>();
        _logger = Substitute.For<ILogger<ApproveProposedSkillChangeCommandHandler>>();

        _handler = new ApproveProposedSkillChangeCommandHandler(
            _proposalRepo, _skillRepo, _cache, _initializer, _knowledgeSync, _logger);
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
            Status = ProposedChangeStatuses.Pending
        };
        var skill = new AgentSkill { Id = skillId, Name = "delete_employee", Description = "Old description", Version = 3 };

        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _skillRepo.GetByIdAsync(skillId, Arg.Any<CancellationToken>()).Returns(skill);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin@klacks"
        }, CancellationToken.None);

        result.Applied.ShouldBeTrue();
        result.NewSkillVersion.ShouldBe(4);
        skill.Description.ShouldBe("Tighter description");
        skill.Version.ShouldBe(4);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Approved);
        proposal.ReviewedBy.ShouldBe("admin@klacks");

        await _skillRepo.Received(1).UpdateAsync(skill, Arg.Any<CancellationToken>());
        _cache.Received(1).InvalidateCache();
        await _initializer.Received(1).InitializeAsync(Arg.Any<CancellationToken>());
        await _knowledgeSync.Received(1).SyncAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_StaleProposal_AutoRejects()
    {
        var skillId = Guid.NewGuid();
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = skillId,
            Field = ProposedChangeFields.Description,
            ValueBefore = "Old description",
            ValueAfter = "Tighter description",
            Status = ProposedChangeStatuses.Pending
        };
        var skill = new AgentSkill { Id = skillId, Description = "Description was changed elsewhere", Version = 5 };

        _proposalRepo.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        _skillRepo.GetByIdAsync(skillId, Arg.Any<CancellationToken>()).Returns(skill);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = proposal.Id,
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Applied.ShouldBeFalse();
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        skill.Description.ShouldBe("Description was changed elsewhere");
        await _skillRepo.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        _cache.DidNotReceive().InvalidateCache();
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

        result.Applied.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("approved");
    }

    [Test]
    public async Task Handle_MissingProposal_ReturnsError()
    {
        _proposalRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ProposedSkillChange?)null);

        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = Guid.NewGuid(),
            ReviewedBy = "admin"
        }, CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("not found");
    }

    [Test]
    public async Task Handle_MissingReviewedBy_ReturnsError()
    {
        var result = await _handler.Handle(new ApproveProposedSkillChangeCommand
        {
            ProposalId = Guid.NewGuid(),
            ReviewedBy = string.Empty
        }, CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("ReviewedBy");
    }
}
