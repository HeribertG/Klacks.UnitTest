// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-10-24 ("ja, nur offene Bestellungen"): an order Klacksy creates is always an open order and never
/// sealed on create; sealing an order through Klacksy runs only through seal_shift, bound to a confirmed preview. The
/// create_shift seed offers no asDraft switch any more and says so, raised in version so existing installations pick it
/// up; the create-shift-order recipe no longer promises a sealed, immediately plannable order; and the knowledge page on
/// the order lifecycle no longer tells Klacksy it can seal orders in bulk or on create.
/// </summary>

using System.Text.Json.Nodes;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class CreateShiftOpenOrderSeedTests
{
    private const string CreateShift = "create_shift";
    private const string CreateShiftOrderRecipe = "create-shift-order";
    private const int LastSealingSkillVersion = 15;
    private const int LastSealingRecipeVersion = 8;
    private const string AsDraftParameter = "asDraft";

    private static readonly string[] SkillSeedPath = ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"];
    private static readonly string[] RecipeSeedPath = ["Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"];
    private static readonly string[] LifecyclePagePath =
        ["Klacks.Api", "Infrastructure", "Persistence", "Seed", "KlacksyKnowledge", "09-shift-lifecycle-order-to-shift.md"];

    private static JsonObject Entry(string[] path, string listProperty, string name) =>
        JsonNode.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(path)))![listProperty]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(entry => entry["name"]!.GetValue<string>() == name);

    [Test]
    public void CreateShiftSeed_OffersNoDraftSwitch_SaysItNeverSeals_AndIsBumped()
    {
        var seed = Entry(SkillSeedPath, "skills", CreateShift);

        seed["version"]!.GetValue<int>().ShouldBeGreaterThan(LastSealingSkillVersion);
        seed["parameters"]!.AsArray().Select(parameter => parameter!["name"]!.GetValue<string>())
            .ShouldNotContain(AsDraftParameter);
        seed["description"]!.GetValue<string>().ShouldContain("never seals");
    }

    [Test]
    public void CreateShiftOrderRecipe_NoLongerPromisesASealedOrder_AndIsBumped()
    {
        var recipe = Entry(RecipeSeedPath, "recipes", CreateShiftOrderRecipe);

        recipe["version"]!.GetValue<int>().ShouldBeGreaterThan(LastSealingRecipeVersion);
        recipe.ToJsonString().ShouldNotContain("immediately plannable");
        recipe["goal"]!.GetValue<string>().ShouldContain("open order");
    }

    [Test]
    public void LifecyclePage_NoLongerOffersBulkSealingOrSealingOnCreate()
    {
        var page = File.ReadAllText(RepositoryRootLocator.RequireFile(LifecyclePagePath));

        page.ShouldNotContain("seal_open_orders");
        page.ShouldContain("seal_shift");
        page.ShouldContain("create_shift");
    }
}
