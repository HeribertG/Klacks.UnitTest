// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Behaviour of the generated collision table on synthetic language data: what counts as a collision, how a UI
/// language resolves it, how a message is matched against it and how the process-wide holder merges registrations.
/// The real language packs are covered by ConversationSignalCollisionTableGuardTests.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ConversationSignalCollisionTableTests
{
    private static ConversationLanguageSignals Language(
        string code,
        string[]? affirmations = null,
        string[]? negations = null,
        string[]? declines = null,
        string[]? cancellations = null,
        string[]? corrections = null,
        string[]? gapIndicators = null) =>
        new(code)
        {
            Affirmations = affirmations ?? [],
            Negations = negations ?? [],
            Declines = declines ?? [],
            Cancellations = cancellations ?? [],
            Corrections = corrections ?? [],
            GapIndicators = gapIndicators ?? []
        };

    [TearDown]
    public void ResetHolder()
    {
        ConversationSignalCollisions.Reset();
    }

    [Test]
    public void Build_RecordsAWordOneLanguageAffirmsAndAnotherNegates()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak", "dobrze"]),
            Language("ms", negations: ["tak", "tidak"])
        ]);

        table.Entries.Keys.ShouldBe(["tak"]);
        var entry = table.Entries["tak"];
        entry.AffirmingLanguages.ShouldBe(["pl"]);
        entry.NegatingLanguages.ShouldBe(["ms"]);
        entry.NegatingCategories.ShouldBe(["negations"]);
    }

    [Test]
    public void Build_IgnoresTheSameWordOnTheSameSideInTwoLanguages()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("da", negations: ["nej"], declines: ["glem det"]),
            Language("sv", negations: ["nej"]),
            Language("nb", cancellations: ["glem det"]),
            Language("pl", affirmations: ["ok"]),
            Language("de", affirmations: ["ok"])
        ]);

        table.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void Build_CollectsEveryNegatingCategoryAndEveryNegatingLanguage()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("aa", affirmations: ["stop it"]),
            Language("bb", declines: ["stop it"]),
            Language("cc", cancellations: ["stop it"]),
            Language("dd", corrections: ["stop it"], negations: ["stop it"])
        ]);

        var entry = table.Entries["stop it"];
        entry.AffirmingLanguages.ShouldBe(["aa"]);
        entry.NegatingLanguages.ShouldBe(["bb", "cc", "dd"]);
        entry.NegatingCategories.ShouldBe(["cancellations", "corrections", "declines", "negations"]);
    }

    [Test]
    public void Build_NormalizesCaseWhitespaceAndUnicodeComposition()
    {
        var composed = "écoute";
        var decomposed = "écoute";

        var table = ConversationSignalCollisionTable.Build(
        [
            Language("aa", affirmations: ["  " + composed.ToUpperInvariant() + " "]),
            Language("bb", negations: [decomposed])
        ]);

        table.Entries.Keys.ShouldBe([composed]);
    }

    [Test]
    public void Build_AWordALanguageListsOnBothSidesIsACollisionThatLanguageCannotSettle()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("xx", affirmations: ["ja"], negations: ["ja"])
        ]);

        table.Entries["ja"].ResolveFor("xx").ShouldBe(CollisionResolution.Ambiguous);
    }

    [Test]
    public void Build_IsDeterministicWhateverTheOrderOfTheLanguages()
    {
        var languages = new[]
        {
            Language("ms", negations: ["tak", "ya"]),
            Language("pl", affirmations: ["tak"]),
            Language("id", affirmations: ["ya"])
        };

        var forward = ConversationSignalCollisionTable.Build(languages).Describe();
        var backward = ConversationSignalCollisionTable.Build(languages.Reverse()).Describe();

        backward.ShouldBe(forward);
    }

    [TestCase("pl", CollisionResolution.Affirming)]
    [TestCase("PL", CollisionResolution.Affirming)]
    [TestCase("pl-PL", CollisionResolution.Affirming)]
    [TestCase("ms", CollisionResolution.Negating)]
    [TestCase("ms-MY", CollisionResolution.Negating)]
    [TestCase("de", CollisionResolution.Ambiguous)]
    [TestCase("", CollisionResolution.Ambiguous)]
    [TestCase("   ", CollisionResolution.Ambiguous)]
    [TestCase(null, CollisionResolution.Ambiguous)]
    public void ResolveFor_SettlesAWordByTheUiLanguageAndLeavesEveryOtherLanguageAmbiguous(
        string? language, CollisionResolution expected)
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"])
        ]);

        table.Entries["tak"].ResolveFor(language).ShouldBe(expected);
    }

    [Test]
    public void ResolveFor_KeepsARegionalPackCodeApart_SoZhCnIsNotZhTw()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("zh-CN", affirmations: ["好"]),
            Language("zh-TW", negations: ["好"])
        ]);

        table.Entries["好"].ResolveFor("zh-CN").ShouldBe(CollisionResolution.Affirming);
        table.Entries["好"].ResolveFor("zh-TW").ShouldBe(CollisionResolution.Negating);
        table.Entries["好"].ResolveFor("zh").ShouldBe(CollisionResolution.Ambiguous);
    }

    [Test]
    public void Build_ALanguageThatUsesTheWordOnlyInsideItsOwnPhrasesIsNeutral_NeverOneThatListsTheWordItself()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"], corrections: ["nie tak"]),
            Language("ms", negations: ["tak"], corrections: ["tak betul"], cancellations: ["tak jadi"]),
            Language("da", declines: ["nej tak", "nej, tak", "ikke nu, tak"]),
            Language("sv", declines: ["nej tack"]),
            Language("cs", declines: ["ne díky"])
        ]);

        var entry = table.Entries["tak"];
        entry.NeutralLanguages.ShouldBe(["da"]);
        entry.AffirmingLanguages.ShouldBe(["pl"]);
        entry.NegatingLanguages.ShouldBe(["ms"]);
        entry.AllLanguages.ShouldBe(["ms", "pl"]);
    }

    [Test]
    public void Build_ThePhraseTokensDecideWhatIsNeutral_NotASubstring()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"]),
            Language("aa", declines: ["takk skal du ha"]),
            Language("bb", declines: ["TAK, tusind tak"]),
            Language("cc", declines: ["tak"]),
            Language("dd", gapIndicators: ["nej tak"])
        ]);

        table.Entries["tak"].NeutralLanguages.ShouldBe(["bb"]);
    }

    [Test]
    public void Build_ANonSegmentedWordIsNeverNeutral()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("aa", affirmations: ["好"]),
            Language("bb", negations: ["好"]),
            Language("cc", declines: ["不好", "好的呀"])
        ]);

        table.Entries["好"].NeutralLanguages.ShouldBeEmpty();
    }

    [TestCase("da", CollisionResolution.Neutral)]
    [TestCase("DA", CollisionResolution.Neutral)]
    [TestCase("da-DK", CollisionResolution.Neutral)]
    [TestCase("pl", CollisionResolution.Affirming)]
    [TestCase("ms", CollisionResolution.Negating)]
    [TestCase("cs", CollisionResolution.Ambiguous)]
    [TestCase("de", CollisionResolution.Ambiguous)]
    [TestCase(null, CollisionResolution.Ambiguous)]
    public void ResolveFor_ANeutralLanguageSettlesNothing_ButIsNotAmbiguousEither(string? language, CollisionResolution expected)
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"], corrections: ["nie tak"]),
            Language("ms", negations: ["tak"]),
            Language("da", declines: ["nej tak"]),
            Language("cs", declines: ["ne díky"])
        ]);

        table.Entries["tak"].ResolveFor(language).ShouldBe(expected);
    }

    [Test]
    public void ResolveFor_ALanguageThatListsTheWordOnBothSidesStaysAmbiguousEvenWithPhrases()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("xx", affirmations: ["ja"], negations: ["ja"], declines: ["nein ja"]),
            Language("yy", negations: ["ja"])
        ]);

        table.Entries["ja"].ResolveFor("xx").ShouldBe(CollisionResolution.Ambiguous);
    }

    [Test]
    public void Match_ANeutralWordIsDroppedFromBothSides_AndIsNeitherSuppressedNorUnresolved()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"]),
            Language("da", declines: ["nej tak"])
        ]);
        string[] negations = ["tak", "tidak"];
        string[] affirmations = ["tak", "dobrze"];

        var asDanish = table.Match("ja tak", ["ja", "tak"], "da");

        asDanish.Neutral.Select(entry => entry.Word).ShouldBe(["tak"]);
        asDanish.Unresolved.ShouldBeEmpty();
        asDanish.SuppressedNegations.ShouldBeEmpty();
        asDanish.SuppressedAffirmations.ShouldBeEmpty();
        asDanish.IsEmpty.ShouldBeFalse();
        asDanish.ExcludeFromNegations(negations, includeUnresolved: false).ShouldBe(["tidak"]);
        asDanish.ExcludeFromAffirmations(affirmations, includeUnresolved: false).ShouldBe(["dobrze"]);
    }

    [Test]
    public void Match_ReturnsASharedEmptyMatchWhenNothingOccurs_AndNeutralCountsAsAMatch()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"]),
            Language("da", declines: ["nej tak"])
        ]);

        table.Match("nej", ["nej"], "da").ShouldBeSameAs(ConversationSignalCollisionMatch.None);
        table.Match("tak", ["tak"], "da").IsEmpty.ShouldBeFalse();
    }

    [Test]
    public void Match_FindsATokenAndSortsItByTheUiLanguage()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"])
        ]);

        var asPolish = table.Match("tak, dobrze", ["tak", "dobrze"], "pl");
        asPolish.SuppressedNegations.ShouldBe(["tak"]);
        asPolish.SuppressedAffirmations.ShouldBeEmpty();
        asPolish.Unresolved.ShouldBeEmpty();

        var asMalay = table.Match("tak, dobrze", ["tak", "dobrze"], "ms");
        asMalay.SuppressedAffirmations.ShouldBe(["tak"]);
        asMalay.SuppressedNegations.ShouldBeEmpty();

        var asGerman = table.Match("tak, dobrze", ["tak", "dobrze"], "de");
        asGerman.Unresolved.Select(entry => entry.Word).ShouldBe(["tak"]);
        asGerman.SuppressedNegations.ShouldBeEmpty();
        asGerman.SuppressedAffirmations.ShouldBeEmpty();
    }

    [Test]
    public void Match_ReturnsNothingForAMessageWithoutACollisionWordOrAnEmptyTable()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"])
        ]);

        table.Match("takt", ["takt"], "pl").IsEmpty.ShouldBeTrue();
        ConversationSignalCollisionTable.Empty.Match("tak", ["tak"], "pl").IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void Match_FindsAPhraseAndANonSegmentedWordBySubstring()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("aa", affirmations: ["go ahead", "好"]),
            Language("bb", negations: ["go ahead", "好"])
        ]);

        table.Match("please go ahead now", ["please", "go", "ahead", "now"], null)
            .Unresolved.Select(entry => entry.Word).ShouldBe(["go ahead"]);
        table.Match("好的", ["好的"], null).Unresolved.Select(entry => entry.Word).ShouldBe(["好"]);
    }

    [Test]
    public void Exclude_DropsOnlyTheSortedWordsAndLeavesTheArrayUntouchedWhenNothingMatched()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"])
        ]);
        string[] negations = ["tak", "tidak"];
        string[] affirmations = ["tak", "dobrze"];

        var asPolish = table.Match("tak", ["tak"], "pl");
        asPolish.ExcludeFromNegations(negations, includeUnresolved: false).ShouldBe(["tidak"]);
        asPolish.ExcludeFromAffirmations(affirmations, includeUnresolved: false).ShouldBeSameAs(affirmations);

        var asMalay = table.Match("tak", ["tak"], "ms");
        asMalay.ExcludeFromAffirmations(affirmations, includeUnresolved: false).ShouldBe(["dobrze"]);
        asMalay.ExcludeFromNegations(negations, includeUnresolved: false).ShouldBeSameAs(negations);

        var asGerman = table.Match("tak", ["tak"], "de");
        asGerman.ExcludeFromNegations(negations, includeUnresolved: false).ShouldBeSameAs(negations);
        asGerman.ExcludeFromNegations(negations, includeUnresolved: true).ShouldBe(["tidak"]);
        asGerman.ExcludeFromAffirmations(affirmations, includeUnresolved: true).ShouldBe(["dobrze"]);
    }

    [Test]
    public void Describe_ListsEveryCollisionWithItsLanguagesAndCategories()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"])
        ]);

        var description = table.Describe();

        description.ShouldContain("collisions: 1");
        description.ShouldContain("tak: yes in [pl], no in [ms] (negations)");
        description.ShouldNotContain("neutral");
        ConversationSignalCollisionTable.Empty.Describe().ShouldContain("collisions: 0");
    }

    [Test]
    public void Describe_NamesTheNeutralLanguages()
    {
        var table = ConversationSignalCollisionTable.Build(
        [
            Language("pl", affirmations: ["tak"]),
            Language("ms", negations: ["tak"]),
            Language("da", declines: ["nej tak"])
        ]);

        table.Describe().ShouldContain("tak: yes in [pl], no in [ms] (negations), neutral in [da]");
    }

    [Test]
    public void Holder_RegistersAndRebuildsFromTheMergedWhole_SoARepeatedOrSplitRegistrationEndsInTheSameTable()
    {
        ConversationSignalCollisions.Current.IsEmpty.ShouldBeTrue();

        ConversationSignalCollisions.Register([Language("pl", affirmations: ["tak"])]);
        ConversationSignalCollisions.Current.IsEmpty.ShouldBeTrue();

        ConversationSignalCollisions.Register([Language("ms", negations: ["tak"])]);
        var once = ConversationSignalCollisions.Current.Describe();
        once.ShouldContain("tak: yes in [pl], no in [ms]");

        ConversationSignalCollisions.Register([Language("ms", negations: ["tak"]), Language("pl", affirmations: ["tak"])]);
        ConversationSignalCollisions.Current.Describe().ShouldBe(once);

        ConversationSignalCollisions.Reset();
        ConversationSignalCollisions.Current.IsEmpty.ShouldBeTrue();
    }
}
