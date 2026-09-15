// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Locks the language-key comparison in AgentRecipe.SynonymsFor. The recipe engine and the turn-eval
/// replay used to resolve pack synonyms by different rules: the engine compared keys case-insensitively
/// while TurnReplayService used a plain Dictionary lookup, which is ordinal. For the region-qualified
/// codes the engine's own comment singles out, a replay asking for "zh-cn" therefore found no synonyms
/// where production found them, and the goldset measured a narrower vocabulary than the engine routes
/// on. Both callers now go through this method; these cases keep them from diverging again.
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
}
