// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Only a gate-passed description proposal can become exported, and who exported it is recorded. Anything else
/// named in the request is reported back as skipped and left exactly as it was.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant.Learning;

using Klacks.Api.Application.Commands.Assistant.Learning;
using Klacks.Api.Application.Handlers.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class MarkProposalsExportedCommandHandlerTests
{
    private const string Reviewer = "admin-user-id";

    private IProposedSkillChangeRepository _proposals = null!;
    private MarkProposalsExportedCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _handler = new MarkProposalsExportedCommandHandler(_proposals);
    }

    private ProposedSkillChange GivenProposal(string status, string field = ProposedChangeFields.Description)
    {
        var proposal = new ProposedSkillChange { Id = Guid.NewGuid(), Status = status, Field = field };
        _proposals.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        return proposal;
    }

    [Test]
    public async Task AGatePassedProposal_BecomesExportedWithItsReviewer()
    {
        var proposal = GivenProposal(ProposedChangeStatuses.GatePassed);

        var result = await _handler.Handle(new MarkProposalsExportedCommand([proposal.Id], Reviewer), CancellationToken.None);

        result.Marked.ShouldBe(1);
        result.Skipped.ShouldBeEmpty();
        proposal.Status.ShouldBe(ProposedChangeStatuses.Exported);
        proposal.ReviewedBy.ShouldBe(Reviewer);
        proposal.ReviewedAt.ShouldNotBeNull();
        await _proposals.Received(1).UpdateAsync(proposal, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task APendingProposal_IsSkippedAndUntouched()
    {
        var proposal = GivenProposal(ProposedChangeStatuses.Pending);

        var result = await _handler.Handle(new MarkProposalsExportedCommand([proposal.Id], Reviewer), CancellationToken.None);

        result.Marked.ShouldBe(0);
        result.Skipped.ShouldHaveSingleItem().ShouldBe(proposal.Id);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Pending);
        await _proposals.DidNotReceive().UpdateAsync(Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>());
    }

    [TestCase(ProposedChangeStatuses.AppliedAuto)]
    [TestCase(ProposedChangeStatuses.Rejected)]
    [TestCase(ProposedChangeStatuses.Exported)]
    [TestCase(ProposedChangeStatuses.BlockedRegression)]
    public async Task AProposalInAnyOtherStatus_IsSkippedAndUntouched(string status)
    {
        var proposal = GivenProposal(status);

        var result = await _handler.Handle(new MarkProposalsExportedCommand([proposal.Id], Reviewer), CancellationToken.None);

        result.Marked.ShouldBe(0);
        result.Skipped.ShouldHaveSingleItem().ShouldBe(proposal.Id);
        proposal.Status.ShouldBe(status);
        await _proposals.DidNotReceive().UpdateAsync(Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGatePassedProposalOfAnotherField_IsSkippedAndUntouched()
    {
        var proposal = GivenProposal(ProposedChangeStatuses.GatePassed, field: ProposedChangeFields.RecipeTriggerNarrowing);

        var result = await _handler.Handle(new MarkProposalsExportedCommand([proposal.Id], Reviewer), CancellationToken.None);

        result.Marked.ShouldBe(0);
        result.Skipped.ShouldHaveSingleItem().ShouldBe(proposal.Id);
        proposal.Status.ShouldBe(ProposedChangeStatuses.GatePassed);
        await _proposals.DidNotReceive().UpdateAsync(Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnknownId_IsSkipped()
    {
        var unknown = Guid.NewGuid();

        var result = await _handler.Handle(new MarkProposalsExportedCommand([unknown], Reviewer), CancellationToken.None);

        result.Skipped.ShouldHaveSingleItem().ShouldBe(unknown);
    }

    [Test]
    public async Task ARepeatedId_IsMarkedOnce()
    {
        var proposal = GivenProposal(ProposedChangeStatuses.GatePassed);

        var result = await _handler.Handle(new MarkProposalsExportedCommand([proposal.Id, proposal.Id], Reviewer), CancellationToken.None);

        result.Marked.ShouldBe(1);
        await _proposals.Received(1).UpdateAsync(proposal, Arg.Any<CancellationToken>());
    }
}
