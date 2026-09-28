// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The verifier answers one question: does the stored index row of a skill start with exactly the text the
/// synchronizer builds from this description. A missing row is "no" - a restore nothing confirmed must stop
/// the run, not pass it.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillIndexStateVerifierTests
{
    private const string Skill = "list_clients";
    private const string Description = "Lists everything about clients.";

    private IKnowledgeIndexRepository _repository = null!;
    private SkillIndexStateVerifier _verifier = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IKnowledgeIndexRepository>();
        _verifier = new SkillIndexStateVerifier(_repository);
    }

    private void GivenIndexText(string text) =>
        _repository.GetByKeysAsync(Arg.Any<IReadOnlyList<(KnowledgeEntryKind Kind, string SourceId)>>(), Arg.Any<CancellationToken>())
            .Returns([new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = Skill, Text = text }]);

    [Test]
    public void ThePrefix_IsNameDotDescriptionNewline()
    {
        SkillEmbeddingTextPrefix.Build(Skill, Description).ShouldBe("list_clients. Lists everything about clients.\n");
    }

    [Test]
    public async Task AnIndexRowWithThisDescription_IsConfirmed()
    {
        GivenIndexText(SkillEmbeddingTextPrefix.Build(Skill, Description) + "Parameters: ");

        (await _verifier.IsIndexedAsync(Skill, Description)).ShouldBeTrue();
    }

    [Test]
    public async Task AnIndexRowWithAnotherDescription_IsNotConfirmed()
    {
        GivenIndexText(SkillEmbeddingTextPrefix.Build(Skill, "Lists the contract data of one client.") + "Parameters: ");

        (await _verifier.IsIndexedAsync(Skill, Description)).ShouldBeFalse();
    }

    [Test]
    public async Task ADescriptionThatIsOnlyAPrefixOfTheIndexedOne_IsNotConfirmed()
    {
        GivenIndexText(SkillEmbeddingTextPrefix.Build(Skill, Description + " More.") + "Parameters: ");

        (await _verifier.IsIndexedAsync(Skill, Description)).ShouldBeFalse();
    }

    [Test]
    public async Task AMissingIndexRow_IsNotConfirmed()
    {
        _repository.GetByKeysAsync(Arg.Any<IReadOnlyList<(KnowledgeEntryKind Kind, string SourceId)>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        (await _verifier.IsIndexedAsync(Skill, Description)).ShouldBeFalse();
    }
}
