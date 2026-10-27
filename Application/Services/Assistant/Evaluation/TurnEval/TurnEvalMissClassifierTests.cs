// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for TurnEvalMissClassifier: every EvalMissKind, the priority order of the rules and the
/// not-applicable gate.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalMissClassifierTests
{
    private const string MutateWithId = "update_contract";
    private const string MutateWithoutId = "create_note";
    private const string AlternativeMutate = "update_contract_v2";
    private const string OtherMutate = "delete_client";
    private const string ReadSkill = "list_contracts";
    private const string AdviseSkill = "suggest_replacement";
    private const string ExplainSkill = "explain_contract";
    private const string UnknownSkill = "no_such_skill";
    private const string RequiredIdParameters = """[{"name":"contractId","type":"String","required":true}]""";
    private const string NoParameters = "[]";

    private static AgentSkill Skill(string name, SkillEffect effect, string parametersJson = NoParameters) => new()
    {
        Name = name,
        Effect = effect,
        ParametersJson = parametersJson
    };

    private static TurnEvalSkillCatalog Catalog() => new(
    [
        Skill(MutateWithId, SkillEffect.Mutate, RequiredIdParameters),
        Skill(MutateWithoutId, SkillEffect.Mutate),
        Skill(AlternativeMutate, SkillEffect.Mutate, RequiredIdParameters),
        Skill(OtherMutate, SkillEffect.Mutate, RequiredIdParameters),
        Skill(ReadSkill, SkillEffect.Read),
        Skill(AdviseSkill, SkillEffect.Advise),
        Skill(ExplainSkill, SkillEffect.Explain),
        Skill(SkillNames.SearchInList, SkillEffect.Read),
        Skill(SkillNames.SearchAndNavigate, SkillEffect.Read),
        Skill(SkillNames.GetPageControls, SkillEffect.Read),
        Skill(SkillNames.GetUserContext, SkillEffect.Read),
        Skill(SkillNames.NavigateTo, SkillEffect.Read)
    ]);

    private static TurnGoldsetItem Item(string expected = MutateWithId, params string[] alternatives) => new()
    {
        Id = "item",
        Message = "message",
        ExpectedTool = expected,
        AlternativeTools = alternatives.ToList()
    };

    private static TurnEvalItemResult Missed(string? chosen, bool? retrieval = true, bool? selection = false, bool? reached = false) => new()
    {
        ItemId = "item",
        ChosenTool = chosen,
        RetrievalHit = retrieval,
        SelectionHit = selection,
        ReachedHit = reached
    };

    private static EvalMissKind Classify(
        TurnGoldsetItem item, TurnEvalItemResult result, TurnReplayResult? replay = null, TurnEvalSkillCatalog? catalog = null) =>
        TurnEvalMissClassifier.Classify(item, result, replay ?? new TurnReplayResult { Success = true }, catalog ?? Catalog());

    [Test]
    public void ItemWithoutExpectedTool_IsNotApplicable()
    {
        var item = new TurnGoldsetItem { Id = "item", Message = "hello" };

        Classify(item, Missed(null)).ShouldBe(EvalMissKind.NotApplicable);
    }

    [Test]
    public void ExcludedItem_IsNotApplicable()
    {
        var result = Missed(ReadSkill);
        result.Excluded = true;

        Classify(Item(), result).ShouldBe(EvalMissKind.NotApplicable);
    }

    [Test]
    public void ErroredItem_IsNotApplicable()
    {
        var result = Missed(null);
        result.Errored = true;

        Classify(Item(), result).ShouldBe(EvalMissKind.NotApplicable);
    }

    [Test]
    public void RecipeItem_IsNotApplicable()
    {
        var item = Item();
        item.ExpectedRecipe = "some_recipe";

        Classify(item, Missed(ReadSkill)).ShouldBe(EvalMissKind.NotApplicable);
    }

    [Test]
    public void RightsRefusalItem_IsNotApplicable()
    {
        var item = Item();
        item.ExpectsRightsRefusal = true;

        Classify(item, Missed(null)).ShouldBe(EvalMissKind.NotApplicable);
    }

    [Test]
    public void ExpectedSkillMissingFromCatalog_IsToolNotRegistered()
    {
        Classify(Item(UnknownSkill), Missed(ReadSkill)).ShouldBe(EvalMissKind.ToolNotRegistered);
    }

    [Test]
    public void ToolNotRegistered_WinsOverNotInToolset()
    {
        Classify(Item(UnknownSkill), Missed(null, retrieval: false, selection: null, reached: null))
            .ShouldBe(EvalMissKind.ToolNotRegistered);
    }

    [Test]
    public void ExpectedSkillMissingButAlternativeInstalled_IsNotToolNotRegistered()
    {
        Classify(Item(UnknownSkill, MutateWithoutId), Missed(null)).ShouldBe(EvalMissKind.NoToolText);
    }

    [Test]
    public void UnknownCatalog_SkipsTheNotRegisteredRule()
    {
        var replay = new TurnReplayResult { Success = true, ToolEffects = { [ReadSkill] = SkillEffect.Read } };
        var result = TurnEvalMissClassifier.Classify(Item(UnknownSkill), Missed(ReadSkill), replay, null);

        result.ShouldBe(EvalMissKind.WrongRead);
    }

    [Test]
    public void ExpectedSkillCutByTheCap_IsNotInToolsetTruncated()
    {
        var replay = new TurnReplayResult
        {
            Success = true,
            ToolsetTrace = new SkillToolsetTrace([MutateWithId], [MutateWithId, ReadSkill], [MutateWithId])
        };

        Classify(Item(), Missed(ReadSkill, retrieval: false, selection: null, reached: null), replay)
            .ShouldBe(EvalMissKind.NotInToolsetTruncated);
    }

    [Test]
    public void AlternativeCutByTheCap_IsNotInToolsetTruncated()
    {
        var replay = new TurnReplayResult
        {
            Success = true,
            ToolsetTrace = new SkillToolsetTrace([], [AlternativeMutate], [AlternativeMutate.ToUpperInvariant()])
        };

        Classify(Item(MutateWithId, AlternativeMutate), Missed(ReadSkill, retrieval: false, selection: null, reached: null), replay)
            .ShouldBe(EvalMissKind.NotInToolsetTruncated);
    }

    [Test]
    public void ExpectedSkillNotInTruncatedNames_IsNotInToolsetNotRetrieved()
    {
        var replay = new TurnReplayResult
        {
            Success = true,
            ToolsetTrace = new SkillToolsetTrace([ReadSkill], [ReadSkill], [])
        };

        Classify(Item(), Missed(ReadSkill, retrieval: false, selection: null, reached: null), replay)
            .ShouldBe(EvalMissKind.NotInToolsetNotRetrieved);
    }

    [Test]
    public void WithoutTrace_MissingExpectedSkill_IsNotInToolsetNotRetrieved()
    {
        Classify(Item(), Missed(ReadSkill, retrieval: false, selection: null, reached: null))
            .ShouldBe(EvalMissKind.NotInToolsetNotRetrieved);
    }

    [Test]
    public void EmptyToolset_WithoutRetrievalVerdict_IsNotInToolsetNotRetrieved()
    {
        Classify(Item(), Missed(null, retrieval: null, selection: null, reached: null))
            .ShouldBe(EvalMissKind.NotInToolsetNotRetrieved);
    }

    [Test]
    public void FirstChoiceHit_IsCorrect()
    {
        Classify(Item(), Missed(MutateWithId, selection: true, reached: true)).ShouldBe(EvalMissKind.Correct);
    }

    [Test]
    public void ReachedOnlyThroughTheFollowUp_IsReachedViaFollowUp()
    {
        Classify(Item(), Missed(ReadSkill, selection: false, reached: true)).ShouldBe(EvalMissKind.ReachedViaFollowUp);
    }

    [Test]
    public void ReachedViaFollowUp_WinsOverLookupFirst()
    {
        Classify(Item(), Missed(ReadSkill, reached: true)).ShouldBe(EvalMissKind.ReachedViaFollowUp);
    }

    [Test]
    public void NoToolChosen_IsNoToolText()
    {
        Classify(Item(), Missed(null)).ShouldBe(EvalMissKind.NoToolText);
    }

    [TestCase(SkillNames.SearchInList)]
    [TestCase(SkillNames.SearchAndNavigate)]
    [TestCase(SkillNames.GetPageControls)]
    [TestCase(SkillNames.GetUserContext)]
    [TestCase(SkillNames.NavigateTo)]
    public void SurfaceSkillInFrontOfAMutation_IsSurfaceSearch(string chosen)
    {
        Classify(Item(), Missed(chosen)).ShouldBe(EvalMissKind.SurfaceSearch);
    }

    [Test]
    public void SurfaceSearch_WinsOverLookupFirst()
    {
        Classify(Item(MutateWithId), Missed(SkillNames.SearchInList)).ShouldBe(EvalMissKind.SurfaceSearch);
    }

    [TestCase(ReadSkill)]
    [TestCase(AdviseSkill)]
    [TestCase(ExplainSkill)]
    public void NonSurfaceReadOfAnIdMutation_IsLookupFirst(string chosen)
    {
        Classify(Item(MutateWithId), Missed(chosen)).ShouldBe(EvalMissKind.LookupFirst);
    }

    [Test]
    public void LookupFirst_ChecksTheAlternativeWhenTheExpectedSkillHasNoCatalogEntry()
    {
        Classify(Item(UnknownSkill, MutateWithId), Missed(ReadSkill)).ShouldBe(EvalMissKind.LookupFirst);
    }

    [Test]
    public void ReadInFrontOfAMutationWithoutIdParameter_IsWrongRead()
    {
        Classify(Item(MutateWithoutId), Missed(ReadSkill)).ShouldBe(EvalMissKind.WrongRead);
    }

    [Test]
    public void OtherMutationChosen_IsWrongWrite()
    {
        Classify(Item(), Missed(OtherMutate)).ShouldBe(EvalMissKind.WrongWrite);
    }

    [Test]
    public void ReadChosenWhereAReadWasExpected_IsWrongRead()
    {
        var item = Item(ReadSkill);

        Classify(item, Missed(AdviseSkill)).ShouldBe(EvalMissKind.WrongRead);
    }

    [Test]
    public void ChosenSkillOutsideTheCatalog_IsWrongRead()
    {
        Classify(Item(), Missed(UnknownSkill)).ShouldBe(EvalMissKind.WrongRead);
    }

    [Test]
    public void ChosenEffectFallsBackToTheReplayToolEffects()
    {
        var replay = new TurnReplayResult { Success = true, ToolEffects = { [UnknownSkill] = SkillEffect.Mutate } };

        Classify(Item(), Missed(UnknownSkill), replay).ShouldBe(EvalMissKind.WrongWrite);
    }

    [Test]
    public void Classifier_DoesNotChangeTheScorerVerdicts()
    {
        var result = Missed(ReadSkill);

        Classify(Item(), result);

        result.SelectionHit.ShouldBe(false);
        result.ReachedHit.ShouldBe(false);
        result.MissKind.ShouldBeNull();
    }
}
