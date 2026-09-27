// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Exercises TurnGoldsetI18nQualityChecks against small in-memory fixtures, so the checking logic is proven
/// before turn-selection-v1-i18n.json exists: a valid set passes every check, and each negative fixture - a
/// malformed id, an id whose locale disagrees with the item, a recipe source, a changed expectation or slot,
/// an uninstalled locale, a language below the per-locale minimum, a repeated message - is flagged.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetI18nQualityChecksTests
{
    private const string ExpectedTool = "list_clients";
    private const string RecipeSourceId = "fixture-recipe";
    private const string Japanese = "ja";
    private const string Arabic = "ar";

    private static readonly string[] Packs = [Japanese, Arabic, "zh-CN"];

    private static readonly string TrainSourceId = FindId(GoldsetPartitioner.IsTrain, "fixture-train");
    private static readonly string HoldoutSourceId = FindId(GoldsetPartitioner.IsHoldout, "fixture-holdout");

    [Test]
    public void AValidSetOfTranslations_HasNoViolationsInAnyCheck()
    {
        var baseById = GivenBaseItems();
        var translations = new List<TurnGoldsetItem>
        {
            Translation(Japanese, TrainSourceId, "クライアントは何人いますか"),
            Translation(Japanese, HoldoutSourceId, "今日のスケジュールを見せて"),
            Translation("zh-CN", TrainSourceId, "我们有多少客户"),
        };

        TurnGoldsetI18nQualityChecks.ValidateIdsSourcesAndPartitions(translations, baseById).ShouldBeEmpty();
        TurnGoldsetI18nQualityChecks.ValidateInheritance(translations, baseById).ShouldBeEmpty();
        TurnGoldsetI18nQualityChecks.ValidateLocales(translations, Packs).ShouldBeEmpty();
        TurnGoldsetI18nQualityChecks.FindMessagesRepeatingAnotherMessage(translations, ["how many clients do we have"]).ShouldBeEmpty();
    }

    [Test]
    public void TheTranslationOfAHoldoutSource_IsHoldoutLikeItsSource()
    {
        var translation = Translation(Japanese, HoldoutSourceId, "今日のスケジュールを見せて");

        GoldsetPartitioner.IsHoldout(translation.Id).ShouldBeTrue();
        TurnGoldsetI18nQualityChecks.ValidateIdsSourcesAndPartitions([translation], GivenBaseItems()).ShouldBeEmpty();
    }

    [TestCase("i18n-ja-fixture-train")]
    [TestCase("para-fixture-train-1")]
    public void AMalformedId_IsFlagged(string id)
    {
        var item = Translation(Japanese, TrainSourceId, "何か");
        item.Id = id;

        TurnGoldsetI18nQualityChecks.ValidateIdsSourcesAndPartitions([item], GivenBaseItems())
            .ShouldContain(violation => violation.Contains("is not i18n-", StringComparison.Ordinal));
    }

    [Test]
    public void AnIdWhoseLocaleDiffersFromTheItemLocale_IsFlagged()
    {
        var item = Translation(Japanese, TrainSourceId, "何か");
        item.Locale = Arabic;

        TurnGoldsetI18nQualityChecks.ValidateIdsSourcesAndPartitions([item], GivenBaseItems())
            .ShouldContain(violation => violation.Contains("differs from the item locale", StringComparison.Ordinal));
    }

    [Test]
    public void AnUnknownOrRecipeSource_IsFlagged()
    {
        var unknown = Translation(Japanese, "not-in-base", "何か");
        var recipe = Translation(Japanese, RecipeSourceId, "設定を始めて");

        var violations = TurnGoldsetI18nQualityChecks.ValidateIdsSourcesAndPartitions([unknown, recipe], GivenBaseItems());

        violations.ShouldContain(violation => violation.Contains("is not an item of", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("not a single-turn item expecting a tool", StringComparison.Ordinal));
    }

    [Test]
    public void AChangedExpectation_IsFlagged()
    {
        var tool = Translation(Japanese, TrainSourceId, "何か");
        tool.ExpectedTool = "navigate_to";
        var slots = Translation(Japanese, TrainSourceId, "別の何か");
        slots.ExpectedSlots = [new TurnGoldsetSlot { Name = "searchTerm", Match = SlotMatchMode.Exact, Value = "Müller" }];
        var route = Translation(Japanese, TrainSourceId, "三つ目");
        route.CurrentRoute = "/workplace/client";

        var violations = TurnGoldsetI18nQualityChecks.ValidateInheritance([tool, slots, route], GivenBaseItems());

        violations.ShouldContain(violation => violation.Contains("expectedTool", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("expectedSlots", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("currentRoute", StringComparison.Ordinal));
    }

    [Test]
    public void ATranslationIntoTheSourceLocaleOrWithoutTranslationSource_IsFlagged()
    {
        var sameLocale = Translation("en", TrainSourceId, "count the clients");
        var wrongSource = Translation(Japanese, TrainSourceId, "何か");
        wrongSource.Source = "curated";

        var violations = TurnGoldsetI18nQualityChecks.ValidateInheritance([sameLocale, wrongSource], GivenBaseItems());

        violations.ShouldContain(violation => violation.Contains("source's own locale", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("is not 'translation'", StringComparison.Ordinal));
    }

    [Test]
    public void GermanOrAnUninstalledLocale_IsFlagged()
    {
        var german = Translation("de", TrainSourceId, "Wie viele Kunden haben wir");
        var klingon = Translation("tlh", TrainSourceId, "qatlh");

        TurnGoldsetI18nQualityChecks.ValidateLocales([german, klingon], Packs).Count.ShouldBe(2);
    }

    [Test]
    public void ALanguageBelowTheMinimum_IsFlagged_CountingBaseAndTranslatedItems()
    {
        var englishBase = Enumerable.Range(0, 30).Select(i => BaseItem($"en-{i}", "en")).ToList();
        var translations = Enumerable.Range(0, 10)
            .Select(i => Translation("en", $"src-{i}", $"en message {i}"))
            .Concat(Enumerable.Range(0, TurnGoldsetI18nQualityChecks.MinItemsPerLocale).Select(i => Translation(Japanese, $"src-{i}", $"ja {i}")))
            .Concat(Enumerable.Range(0, TurnGoldsetI18nQualityChecks.MinItemsPerLocale - 1).Select(i => Translation(Arabic, $"src-{i}", $"ar {i}")))
            .ToList();

        var violations = TurnGoldsetI18nQualityChecks.FindLocalesBelowMinimum(translations, englishBase, [Japanese, Arabic]);

        violations.ShouldContain(violation => violation.StartsWith($"locale '{Arabic}'", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.StartsWith("locale 'fr'", StringComparison.Ordinal));
        violations.ShouldNotContain(violation => violation.StartsWith("locale 'en'", StringComparison.Ordinal));
        violations.ShouldNotContain(violation => violation.StartsWith($"locale '{Japanese}'", StringComparison.Ordinal));
    }

    [Test]
    public void AMessageRepeatingAnotherGoldsetOrAnotherTranslation_IsFlagged()
    {
        var repeatsBase = Translation(Japanese, TrainSourceId, "How many clients do we have ");
        var first = Translation(Japanese, HoldoutSourceId, "同じ文");
        var second = Translation("zh-CN", HoldoutSourceId, "同じ文");

        var violations = TurnGoldsetI18nQualityChecks.FindMessagesRepeatingAnotherMessage(
            [repeatsBase, first, second], ["how many clients do we have"]);

        violations.Count.ShouldBe(2);
    }

    private static Dictionary<string, TurnGoldsetItem> GivenBaseItems()
    {
        var items = new[]
        {
            BaseItem(TrainSourceId, "en", "how many clients do we have"),
            BaseItem(HoldoutSourceId, "en", "show me the schedule for today"),
            new TurnGoldsetItem { Id = RecipeSourceId, Message = "start the setup", Locale = "en", ExpectedRecipe = "setup-consultation" },
        };

        return items.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    private static TurnGoldsetItem BaseItem(string id, string locale, string message = "a base message") => new()
    {
        Id = id,
        Message = message,
        Locale = locale,
        ExpectedTool = ExpectedTool,
        Source = "curated"
    };

    private static TurnGoldsetItem Translation(string locale, string sourceId, string message) => new()
    {
        Id = GoldsetTranslationId.Compose(locale, sourceId),
        Message = message,
        Locale = locale,
        ExpectedTool = ExpectedTool,
        Source = TurnGoldsetI18nQualityChecks.TranslationSource,
        Comment = $"Translation of {sourceId}."
    };

    private static string FindId(Func<string, bool> predicate, string stem) =>
        Enumerable.Range(0, 1000).Select(i => $"{stem}-{i}").First(predicate);
}
