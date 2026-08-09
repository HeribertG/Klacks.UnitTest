// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TriggerKeywordFormat.ReadGrouped/ReadFlat. ReadGrouped runs once per skill on every
/// chat message and reads the column straight off the parsed JsonDocument, so these tests pin the
/// shapes the column can hold: the language-keyed object, the historical flat array, and the malformed
/// values that must not take the surrounding phrases down with them.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class TriggerKeywordFormatTests
{
    [Test]
    public void ReadGrouped_LanguageKeyedObject_KeepsGroups()
    {
        var groups = TriggerKeywordFormat.ReadGrouped("""{"de":["urlaub","ferien"],"en":["vacation"]}""");

        Assert.Multiple(() =>
        {
            Assert.That(groups["de"], Is.EqualTo(new[] { "urlaub", "ferien" }));
            Assert.That(groups["en"], Is.EqualTo(new[] { "vacation" }));
        });
    }

    [Test]
    public void ReadGrouped_LegacyFlatArray_ReadsAsUndeterminedGroup()
    {
        var groups = TriggerKeywordFormat.ReadGrouped("""["urlaub","ferien"]""");

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[SkillPhraseLanguages.Undetermined], Is.EqualTo(new[] { "urlaub", "ferien" }));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("[]")]
    [TestCase("{}")]
    public void ReadGrouped_EmptyInput_ReturnsEmpty(string? json)
    {
        Assert.That(TriggerKeywordFormat.ReadGrouped(json), Is.Empty);
    }

    [Test]
    public void ReadGrouped_MalformedJson_ReturnsEmpty()
    {
        Assert.That(TriggerKeywordFormat.ReadGrouped("{not json"), Is.Empty);
    }

    [Test]
    public void ReadGrouped_NullEntryInsideGroup_KeepsTheRemainingPhrases()
    {
        var groups = TriggerKeywordFormat.ReadGrouped("""{"de":["urlaub",null,"ferien"]}""");

        Assert.That(groups["de"], Is.EqualTo(new[] { "urlaub", "ferien" }));
    }

    [Test]
    public void ReadGrouped_NullGroup_DoesNotDiscardTheOtherGroups()
    {
        var groups = TriggerKeywordFormat.ReadGrouped("""{"de":null,"en":["vacation"]}""");

        Assert.Multiple(() =>
        {
            Assert.That(groups["de"], Is.Empty);
            Assert.That(groups["en"], Is.EqualTo(new[] { "vacation" }));
        });
    }

    [Test]
    public void ReadGrouped_WronglyTypedGroup_ReturnsEmpty()
    {
        Assert.That(TriggerKeywordFormat.ReadGrouped("""{"de":5}"""), Is.Empty);
    }

    [Test]
    public void ReadGrouped_WronglyTypedPhrase_ReturnsEmpty()
    {
        Assert.That(TriggerKeywordFormat.ReadGrouped("""{"de":["urlaub",5]}"""), Is.Empty);
    }

    [Test]
    public void ReadFlat_ReturnsPhrasesOfEveryLanguage()
    {
        var flat = TriggerKeywordFormat.ReadFlat("""{"de":["urlaub"],"en":["vacation"]}""");

        Assert.That(flat, Is.EquivalentTo(new[] { "urlaub", "vacation" }));
    }

    [Test]
    public void ReadFlat_NullGroup_DoesNotThrow()
    {
        Assert.That(TriggerKeywordFormat.ReadFlat("""{"de":null,"en":["vacation"]}"""),
            Is.EqualTo(new[] { "vacation" }));
    }
}
