// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Locks the language-key comparison in AgentRecipe.SynonymsFor and its W1b counterpart VetoesFor. The
/// recipe engine and the turn-eval replay used to resolve pack synonyms by different rules: the engine
/// compared keys case-insensitively while TurnReplayService used a plain Dictionary lookup, which is
/// ordinal. For the region-qualified codes the engine's own comment singles out, a replay asking for
/// "zh-cn" therefore found no synonyms where production found them, and the goldset measured a narrower
/// vocabulary than the engine routes on. Both callers now go through this method; these cases keep them
/// from diverging again.
/// VetoesFor is covered here rather than in a second fixture because both resolve through one shared
/// private lookup: the divergence this file exists to prevent was itself a duplicated lookup, so the
/// tests belong where the sharing is visible. The fixture name still says SynonymsFor for continuity
/// with the entry that introduced it.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Domain.Models.Assistant;

[TestFixture]
public class AgentRecipeSynonymsForTests
{
    private const string SpanishPhrase = "mover el grupo bajo otro grupo padre";
    private const string ChinesePhrase = "把组移动到另一个父组";

    private static AgentRecipe RecipeWithInstalledPacks() => new()
    {
        Name = "move-group",
        Synonyms = new Dictionary<string, List<string>>
        {
            ["es"] = [SpanishPhrase],
            ["zh-CN"] = [ChinesePhrase]
        }
    };

    [Test]
    public void ResolvesAnExactLanguageKey()
    {
        RecipeWithInstalledPacks().SynonymsFor("es")!.ShouldContain(SpanishPhrase);
    }

    [TestCase("ES")]
    [TestCase("Es")]
    public void ResolvesACasingVariantOfTheLanguageKey(string language)
    {
        RecipeWithInstalledPacks().SynonymsFor(language)!.ShouldContain(SpanishPhrase);
    }

    /// <summary>
    /// The regression the ordinal lookup produced: the pack is installed under "zh-CN", a caller asks
    /// in lower case, and the synonyms silently disappear.
    /// </summary>
    [TestCase("zh-cn")]
    [TestCase("ZH-CN")]
    [TestCase("zh-Cn")]
    public void ResolvesARegionQualifiedCodeRegardlessOfCasing(string language)
    {
        RecipeWithInstalledPacks().SynonymsFor(language)!.ShouldContain(ChinesePhrase);
    }

    [Test]
    public void DoesNotFallBackToASiblingRegion()
    {
        RecipeWithInstalledPacks().SynonymsFor("zh-TW").ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    public void WithoutALanguageThereAreNoPackSynonyms(string? language)
    {
        RecipeWithInstalledPacks().SynonymsFor(language).ShouldBeNull();
    }

    [Test]
    public void ARecipeNoPackWasInstalledForResolvesToNull()
    {
        new AgentRecipe { Name = "move-group" }.SynonymsFor("es").ShouldBeNull();
    }

    private const string SpanishQuestionVeto = "cómo ";
    private const string ChineseQuestionVeto = "为什么";

    private static AgentRecipe RecipeWithInstalledVetoes() => new()
    {
        Name = "move-group",
        Vetoes = new Dictionary<string, List<string>>
        {
            ["es"] = [SpanishQuestionVeto],
            ["zh-CN"] = [ChineseQuestionVeto]
        }
    };

    [Test]
    public void VetoesResolveAnExactLanguageKey()
    {
        RecipeWithInstalledVetoes().VetoesFor("es")!.ShouldContain(SpanishQuestionVeto);
    }

    [TestCase("ES")]
    [TestCase("Es")]
    public void VetoesResolveACasingVariantOfTheLanguageKey(string language)
    {
        RecipeWithInstalledVetoes().VetoesFor(language)!.ShouldContain(SpanishQuestionVeto);
    }

    /// <summary>
    /// The same ordinal-lookup regression the synonyms above pin, for the veto column: a veto that
    /// resolves for the engine but not for the replay is a veto the goldset cannot see.
    /// </summary>
    [TestCase("zh-cn")]
    [TestCase("ZH-CN")]
    [TestCase("zh-Cn")]
    public void VetoesResolveARegionQualifiedCodeRegardlessOfCasing(string language)
    {
        RecipeWithInstalledVetoes().VetoesFor(language)!.ShouldContain(ChineseQuestionVeto);
    }

    [Test]
    public void VetoesDoNotFallBackToASiblingRegion()
    {
        RecipeWithInstalledVetoes().VetoesFor("zh-TW").ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    public void WithoutALanguageThereAreNoPackVetoes(string? language)
    {
        RecipeWithInstalledVetoes().VetoesFor(language).ShouldBeNull();
    }

    [Test]
    public void ARecipeNoVetoPackWasInstalledForResolvesToNull()
    {
        new AgentRecipe { Name = "move-group" }.VetoesFor("es").ShouldBeNull();
    }

    /// <summary>
    /// Guards the refactor both methods now share. A VetoesFor that returned the Synonyms map - the
    /// obvious copy/paste failure - would pass every single-column test above, because each fixture
    /// populates only one column. This is the case that tells the two apart, and it is the one that
    /// would silently widen or narrow a veto at runtime.
    /// </summary>
    [Test]
    public void TheTwoPackColumnsDoNotReadEachOther()
    {
        var recipe = new AgentRecipe
        {
            Name = "move-group",
            Synonyms = new Dictionary<string, List<string>> { ["es"] = [SpanishPhrase] },
            Vetoes = new Dictionary<string, List<string>> { ["zh-CN"] = [ChineseQuestionVeto] }
        };

        recipe.SynonymsFor("es")!.ShouldContain(SpanishPhrase);
        recipe.VetoesFor("es").ShouldBeNull();
        recipe.VetoesFor("zh-CN")!.ShouldContain(ChineseQuestionVeto);
        recipe.SynonymsFor("zh-CN").ShouldBeNull();
    }
}
