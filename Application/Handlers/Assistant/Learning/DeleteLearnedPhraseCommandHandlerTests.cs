// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for withdrawing a row of the "phrasings" section. The case worth pinning is the description the
/// loop applied on its own: discarding it has to put the old description back, or the card would report a
/// change as discarded while it stays live in the skill catalogue.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant.Learning;

using Klacks.Api.Application.Commands.Assistant.Learning;
using Klacks.Api.Application.Handlers.Assistant.Learning;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class DeleteLearnedPhraseCommandHandlerTests
{
    private const string Before = "Lists everything about clients.";
    private const string After = "Lists the contract data of one client.";

    private ISkillPhraseRepository _phrases = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private DeleteLearnedPhraseCommandHandler _handler = null!;
    private AgentSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _phrases.SetStatusAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _skills = Substitute.For<IAgentSkillRepository>();
        _refresher = Substitute.For<ISkillCatalogRefresher>();

        _skill = new AgentSkill { Id = Guid.NewGuid(), Name = "list_clients", Description = After, Version = 4 };
        _skills.GetByIdAsync(_skill.Id, Arg.Any<CancellationToken>()).Returns(_skill);

        _handler = new DeleteLearnedPhraseCommandHandler(
            _phrases, _proposals, _skills, _refresher,
            Substitute.For<ILogger<DeleteLearnedPhraseCommandHandler>>());
    }

    private ProposedSkillChange Given(string status, string currentDescription = After)
    {
        _skill.Description = currentDescription;

        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            SkillId = _skill.Id,
            SkillName = _skill.Name,
            Field = ProposedChangeFields.Description,
            ValueBefore = Before,
            ValueAfter = After,
            Status = status
        };

        _proposals.GetByIdAsync(proposal.Id, Arg.Any<CancellationToken>()).Returns(proposal);
        return proposal;
    }

    [Test]
    public async Task ALearnedPhrase_IsWithdrawnWithoutTouchingAnySkill()
    {
        var phraseId = Guid.NewGuid();
        _phrases.SetStatusAsync(phraseId, SkillPhraseStatuses.Rejected, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new DeleteLearnedPhraseCommand(phraseId), CancellationToken.None);

        result.Found.ShouldBeTrue();
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAutomaticallyAppliedDescription_IsPutBackWhenItIsDiscarded()
    {
        var proposal = Given(ProposedChangeStatuses.AppliedAuto);

        var result = await _handler.Handle(new DeleteLearnedPhraseCommand(proposal.Id), CancellationToken.None);

        result.Found.ShouldBeTrue();
        _skill.Description.ShouldBe(Before);
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        await _skills.Received(1).UpdateAsync(_skill, Arg.Any<CancellationToken>());
        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Overwriting a description something else changed since would be exactly the silent data loss the
    // stale check in the approval path exists to prevent.
    [Test]
    public async Task ADescriptionThatChangedSince_IsLeftAlone()
    {
        var proposal = Given(ProposedChangeStatuses.AppliedAuto, "Written by an administrator in the meantime.");

        await _handler.Handle(new DeleteLearnedPhraseCommand(proposal.Id), CancellationToken.None);

        _skill.Description.ShouldBe("Written by an administrator in the meantime.");
        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    // Reporting plain success here would tell the administrator the automatic change was undone while a
    // foreign description stays live - the one outcome the card cannot show.
    [Test]
    public async Task ADescriptionThatChangedSince_IsReportedAsAConflict()
    {
        var proposal = Given(ProposedChangeStatuses.AppliedAuto, "Written by an administrator in the meantime.");

        var result = await _handler.Handle(new DeleteLearnedPhraseCommand(proposal.Id), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Conflict.ShouldBeTrue();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    // A proposal that never went live has nothing to put back, so discarding it is plain success.
    [Test]
    public async Task AProposalThatWasNeverApplied_IsNotReportedAsAConflict()
    {
        var proposal = Given(ProposedChangeStatuses.BlockedRegression);

        var result = await _handler.Handle(new DeleteLearnedPhraseCommand(proposal.Id), CancellationToken.None);

        result.Conflict.ShouldBeFalse();
        result.Error.ShouldBeNull();
    }

    [TestCase(ProposedChangeStatuses.Pending)]
    [TestCase(ProposedChangeStatuses.BlockedRegression)]
    public async Task AProposalThatWasNeverApplied_IsOnlyMarkedRejected(string status)
    {
        var proposal = Given(status);

        await _handler.Handle(new DeleteLearnedPhraseCommand(proposal.Id), CancellationToken.None);

        proposal.Status.ShouldBe(ProposedChangeStatuses.Rejected);
        await _skills.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _refresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnknownId_ReportsNotFound()
    {
        _proposals.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ProposedSkillChange?)null);

        (await _handler.Handle(new DeleteLearnedPhraseCommand(Guid.NewGuid()), CancellationToken.None))
            .Found.ShouldBeFalse();
    }
}
