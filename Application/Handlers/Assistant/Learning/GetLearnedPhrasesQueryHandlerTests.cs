// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins which proposal rows the "Klacksy learned" card may show as phrasings. proposed_skill_changes holds
/// two unrelated kinds of row: sharpened skill descriptions, which the card exists for, and recipe trigger
/// narrowings, which name a recipe and quote the utterance that wrongly matched it. Listing the second kind
/// as a description would offer an administrator an edit that writes a user utterance back as a skill
/// description. The exclusion has to happen in SQL, before the newest-first window is taken: narrowing rows
/// are written with status pending like every other proposal, so a burst of them would fill the window and
/// leave the card empty after an in-memory filter.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant.Learning;
using Klacks.Api.Application.Queries.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Assistant.Learning;

[TestFixture]
public class GetLearnedPhrasesQueryHandlerTests
{
    private const int Limit = 50;
    private const string DescriptionSkill = "list_clients";
    private const string NarrowedRecipe = "create-group";

    private ISkillPhraseRepository _phrases = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private ILearnedArtefactResolver _artefacts = null!;
    private ISkillLearningFitnessRepository _fitness = null!;
    private GetLearnedPhrasesQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _artefacts = Substitute.For<ILearnedArtefactResolver>();
        _fitness = Substitute.For<ISkillLearningFitnessRepository>();
        _phrases
            .GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _artefacts.ListActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _handler = new GetLearnedPhrasesQueryHandler(
            _phrases,
            _proposals,
            _artefacts,
            _fitness,
            Substitute.For<ILogger<GetLearnedPhrasesQueryHandler>>());
    }

    [Test]
    public async Task Handle_ARecipeTriggerNarrowing_IsNotListedAsAPhrasing()
    {
        GivenProposals(
            Proposal(ProposedChangeFields.Description, DescriptionSkill),
            Proposal(ProposedChangeFields.RecipeTriggerNarrowing, NarrowedRecipe));

        var rows = await _handler.Handle(new GetLearnedPhrasesQuery(Limit), CancellationToken.None);

        rows.Count.ShouldBe(1);
        rows[0].SkillName.ShouldBe(DescriptionSkill);
    }

    [Test]
    public async Task Handle_AsksTheStoreForDescriptionRowsOnly()
    {
        GivenProposals(Proposal(ProposedChangeFields.Description, DescriptionSkill));

        await _handler.Handle(new GetLearnedPhrasesQuery(Limit), CancellationToken.None);

        await _proposals.Received(1).GetByStatusesAsync(
            Arg.Any<IReadOnlyList<string>>(),
            ProposedChangeFields.Description,
            Limit,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithOnlyRecipeTriggerNarrowings_ListsNothing()
    {
        GivenProposals(Proposal(ProposedChangeFields.RecipeTriggerNarrowing, NarrowedRecipe));

        var rows = await _handler.Handle(new GetLearnedPhrasesQuery(Limit), CancellationToken.None);

        rows.ShouldBeEmpty();
    }

    private void GivenProposals(params ProposedSkillChange[] proposals) =>
        _proposals
            .GetByStatusesAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => proposals
                .Where(proposal => string.Equals(
                    proposal.Field, (string)call[1], StringComparison.Ordinal))
                .ToList());

    private static ProposedSkillChange Proposal(string field, string skillName) => new()
    {
        Id = Guid.NewGuid(),
        SkillName = skillName,
        Field = field,
        ValueAfter = "Zeig mir die Kunden",
        Status = ProposedChangeStatuses.Pending
    };
}
