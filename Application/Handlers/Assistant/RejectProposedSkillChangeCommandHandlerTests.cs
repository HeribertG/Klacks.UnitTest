// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for rejecting a description proposal. The status guard is what matters: the learning loop
/// consumes the open proposals by itself, so regression-blocked is the state a rejection now arrives in,
/// and a guard that only knew "pending" would leave the only rejectable rows unrejectable.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class RejectProposedSkillChangeCommandHandlerTests
{
    private IProposedSkillChangeRepository _proposals = null!;
    private RejectProposedSkillChangeCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _handler = new RejectProposedSkillChangeCommandHandler(
            _proposals, Substitute.For<ILogger<RejectProposedSkillChangeCommandHandler>>());
    }

    private ProposedSkillChange Given(string status)
    {
        var proposal = new ProposedSkillChange { Id = Guid.NewGuid(), Status = status };
        _proposals.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        return proposal;
    }

    private async Task<RejectProposedSkillChangeResult> RejectAsync(Guid id) =>
        await _handler.Handle(
            new RejectProposedSkillChangeCommand { ProposalId = id, ReviewedBy = "admin" }, CancellationToken.None);

    [TestCase(ProposedChangeStatuses.Pending)]
    [TestCase(ProposedChangeStatuses.BlockedRegression)]
    public async Task AnOpenOrBlockedProposal_CanBeRejected(string status)
    {
        var proposal = Given(status);

        (await RejectAsync(proposal.Id)).Rejected.ShouldBeTrue();

        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        proposal.ReviewedBy.ShouldBe("admin");
    }

    // Undoing an applied change means restoring the old description, which the learning card's delete does.
    [TestCase(ProposedChangeStatuses.AppliedAuto)]
    [TestCase(ProposedChangeStatuses.Approved)]
    [TestCase(ProposedChangeStatuses.Rejected)]
    public async Task AProposalThatWasAlreadyDecided_IsNotRejectedAgain(string status)
    {
        var proposal = Given(status);

        var result = await RejectAsync(proposal.Id);

        result.Rejected.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain(status);
        await _proposals.DidNotReceive().UpdateAsync(
            Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AMissingProposal_ReportsNotFound()
    {
        _proposals.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ProposedSkillChange?)null);

        (await RejectAsync(Guid.NewGuid())).Error.ShouldNotBeNull().ShouldContain("not found");
    }
}
