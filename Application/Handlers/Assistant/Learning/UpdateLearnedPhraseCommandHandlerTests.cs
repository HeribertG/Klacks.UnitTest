// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins how the handler tells the learning card's two id spaces apart. The phrase store is scoped to
/// source Learned, so an id belonging to a seed, language-pack or admin phrase never reaches the phrase
/// branch: it falls through to the proposal store and, finding nothing there either, ends as not found -
/// a 404 - rather than as a wording conflict from a write the source filter rejected.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant.Learning;

using Klacks.Api.Application.Commands.Assistant.Learning;
using Klacks.Api.Application.Handlers.Assistant.Learning;
using Klacks.Api.Application.Services.Assistant;

[TestFixture]
public class UpdateLearnedPhraseCommandHandlerTests
{
    private const string NewText = "umsatzstatistik pro kunde";

    private ISkillPhraseRepository _phrases = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private UpdateLearnedPhraseCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _refresher = Substitute.For<ISkillCatalogRefresher>();
        _handler = new UpdateLearnedPhraseCommandHandler(_phrases, _proposals, _refresher);
    }

    [Test]
    public async Task Handle_LearnedPhrase_IsRewrittenAndRefreshesTheCatalog()
    {
        var id = Guid.NewGuid();
        _phrases.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new SkillPhrase { Id = id });
        _phrases.TryUpdatePhraseTextAsync(id, NewText, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new UpdateLearnedPhraseCommand(id, NewText, null), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Conflict.ShouldBeFalse();
        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_IdOutsideTheCardsScope_IsNotFoundRatherThanAConflict()
    {
        var id = Guid.NewGuid();
        _phrases.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((SkillPhrase?)null);
        _proposals.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((ProposedSkillChange?)null);

        var result = await _handler.Handle(new UpdateLearnedPhraseCommand(id, NewText, null), CancellationToken.None);

        result.Found.ShouldBeFalse();
        result.Conflict.ShouldBeFalse();
        await _phrases.DidNotReceiveWithAnyArgs().TryUpdatePhraseTextAsync(default, default!, default);
        await _refresher.DidNotReceiveWithAnyArgs().RefreshAsync(default!, default);
    }
}
