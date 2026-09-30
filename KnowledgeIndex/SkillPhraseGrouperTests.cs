// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that SkillPhraseGrouper produces a total order: the phrase lists feed the hashed embedding
/// text, so any dependence on the arrival order of the rows (skill_phrase is read without ORDER BY)
/// would change the text hash between runs and re-embed the whole index on every start.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Shouldly;
using NUnit.Framework;

namespace Klacks.UnitTest.KnowledgeIndex;

[TestFixture]
public class SkillPhraseGrouperTests
{
    private const string Owner = "S";
    private const int Permutations = 50;
    private const int Seed = 20260930;

    [Test]
    public void Group_TiedSortOrder_YieldsIdenticalListsForEveryInputOrder()
    {
        // Arrange
        var phrases = new List<SkillPhrase>
        {
            Phrase(SkillPhraseKinds.Keyword, "de", "plan", 0),
            Phrase(SkillPhraseKinds.Keyword, "de", "dienstplan", 0),
            Phrase(SkillPhraseKinds.Keyword, "de", "einsatz", 0),
            Phrase(SkillPhraseKinds.Synonym, "de", "Plan", 0),
            Phrase(SkillPhraseKinds.Synonym, "de", "plan", 0),
            Phrase(SkillPhraseKinds.Synonym, "de", "schicht", 0),
            Phrase(SkillPhraseKinds.Synonym, "en", "roster", 0),
            Phrase(SkillPhraseKinds.Synonym, "en", "shift", 0),
        };

        var expected = SkillPhraseGrouper.Group(phrases)[(SkillPhraseOwnerKinds.Skill, Owner)];
        var random = new Random(Seed);

        for (var i = 0; i < Permutations; i++)
        {
            // Act
            var shuffled = phrases.OrderBy(_ => random.Next()).ToList();
            var actual = SkillPhraseGrouper.Group(shuffled)[(SkillPhraseOwnerKinds.Skill, Owner)];

            // Assert
            actual.Keywords.ShouldBe(expected.Keywords);
            actual.Synonyms.ShouldBe(expected.Synonyms);
        }
    }

    [Test]
    public void Group_TiedSortOrderWithCaseVariants_KeepsTheOrdinallySmallerCasingRegardlessOfInputOrder()
    {
        // Arrange
        var upperFirst = new List<SkillPhrase>
        {
            Phrase(SkillPhraseKinds.Synonym, "de", "Plan", 0),
            Phrase(SkillPhraseKinds.Synonym, "de", "plan", 0),
        };
        var lowerFirst = upperFirst.AsEnumerable().Reverse().ToList();

        // Act
        var fromUpperFirst = SkillPhraseGrouper.Group(upperFirst)[(SkillPhraseOwnerKinds.Skill, Owner)];
        var fromLowerFirst = SkillPhraseGrouper.Group(lowerFirst)[(SkillPhraseOwnerKinds.Skill, Owner)];

        // Assert
        fromUpperFirst.Synonyms.ShouldBe(["Plan"]);
        fromLowerFirst.Synonyms.ShouldBe(["Plan"]);
    }

    [Test]
    public void Group_DistinctSortOrder_KeepsTheStoredOrderAndIgnoresThePhraseText()
    {
        // Arrange
        var phrases = new List<SkillPhrase>
        {
            Phrase(SkillPhraseKinds.Keyword, "de", "zeta", 0),
            Phrase(SkillPhraseKinds.Keyword, "de", "alpha", 1),
            Phrase(SkillPhraseKinds.Synonym, "de", "zulu", 0),
            Phrase(SkillPhraseKinds.Synonym, "de", "alfa", 1),
        };
        phrases.Reverse();

        // Act
        var set = SkillPhraseGrouper.Group(phrases)[(SkillPhraseOwnerKinds.Skill, Owner)];

        // Assert
        set.Keywords.ShouldBe(["zeta", "alpha"]);
        set.Synonyms.ShouldBe(["zulu", "alfa"]);
    }

    private static SkillPhrase Phrase(string kind, string language, string phrase, int sortOrder) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerKind = SkillPhraseOwnerKinds.Skill,
            OwnerName = Owner,
            Language = language,
            Kind = kind,
            Phrase = phrase,
            SortOrder = sortOrder
        };
}
