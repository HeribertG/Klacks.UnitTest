// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the data-driven recipe execution plan: ask steps whose slot is filled are skipped
/// (don't re-ask what was said / GUID fast-path), search steps capture the lone matching id into a slot
/// and deactivate on an ambiguous result, slot values are injected into the forced skill's parameters,
/// non-injected slots surface as known values, and resume restores the slot bag and step index.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeExecutionPlanTests
{
    private const string GroupGuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ClientGuid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private static List<RecipeStep> AddClientToGroupSteps() =>
    [
        new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "groupName", Prompt = "Which group?" },
        new RecipeStep
        {
            Kind = RecipeStepKinds.Search, Skill = "list_groups",
            Inject = new Dictionary<string, string> { ["searchTerm"] = "$groupName" },
            Capture = "Groups[].Id as groupId"
        },
        new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "clientName", Prompt = "Which employee?" },
        new RecipeStep
        {
            Kind = RecipeStepKinds.Search, Skill = "search_employees",
            Inject = new Dictionary<string, string> { ["searchTerm"] = "$clientName" },
            Capture = "Results[].Id as clientId"
        },
        new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "validFrom", Prompt = "From when?" },
        new RecipeStep
        {
            Kind = RecipeStepKinds.Mutate, Skill = "add_client_to_group",
            Inject = new Dictionary<string, string> { ["clientId"] = "$clientId", ["groupId"] = "$groupId" }
        }
    ];

    private static LLMFunctionCall SuccessCall(string skill, string dataJson) => new()
    {
        FunctionName = skill,
        Success = true,
        Result = "Skill executed. Data: " + dataJson
    };

    [Test]
    public void Fresh_Plan_With_No_Prefill_Stops_At_First_Ask()
    {
        // Arrange
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());

        // Act
        plan.AdvanceOverSatisfied();

        // Assert
        Assert.That(plan.CurrentIsAsk, Is.True);
        Assert.That(plan.CurrentAskPrompt, Is.EqualTo("Which group?"));
    }

    [Test]
    public void Prefilled_Ask_Slots_Are_Skipped_To_First_Search()
    {
        // Arrange — opening message already named the group ("don't re-ask")
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Bern" });

        // Act
        plan.AdvanceOverSatisfied();

        // Assert
        Assert.That(plan.CurrentIsAsk, Is.False);
        Assert.That(plan.CurrentSkill, Is.EqualTo("list_groups"));
    }

    [Test]
    public void Search_Step_Injects_Slot_Value_Into_Parameter()
    {
        // Arrange
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Bern" });
        plan.AdvanceOverSatisfied();

        // Act
        var injections = plan.GetParameterInjections("list_groups");

        // Assert
        Assert.That(injections["searchTerm"], Is.EqualTo("Bern"));
    }

    [Test]
    public void Lone_Match_Is_Captured_And_Plan_Advances()
    {
        // Arrange
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Bern" });
        plan.AdvanceOverSatisfied();

        // Act
        plan.Observe([SuccessCall("list_groups", $"{{\"Groups\":[{{\"Id\":\"{GroupGuid}\",\"Name\":\"Bern\"}}],\"TotalCount\":1}}")]);

        // Assert — groupId captured, advanced to the next ask (clientName)
        Assert.That(plan.Slots["groupId"], Is.EqualTo(GroupGuid));
        Assert.That(plan.CurrentIsAsk, Is.True);
        Assert.That(plan.CurrentAskPrompt, Is.EqualTo("Which employee?"));
    }

    [Test]
    public void Ambiguous_Result_Rewinds_To_Ask_For_Disambiguation()
    {
        // Arrange
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Be" });
        plan.AdvanceOverSatisfied();

        // Act — two groups match, so capture is impossible; instead of deactivating, the plan clears the
        // input slot and rewinds to the ask so the user can disambiguate.
        plan.Observe([SuccessCall("list_groups",
            $"{{\"Groups\":[{{\"Id\":\"{GroupGuid}\"}},{{\"Id\":\"{ClientGuid}\"}}],\"TotalCount\":2}}")]);

        // Assert — still active, back on the groupName ask, slot cleared
        Assert.That(plan.IsActive, Is.True);
        Assert.That(plan.CurrentIsAsk, Is.True);
        Assert.That(plan.CurrentAskPrompt, Is.EqualTo("Which group?"));
        Assert.That(plan.Slots.ContainsKey("groupName"), Is.False);
    }

    [Test]
    public void Second_Ambiguous_Result_After_Rewind_Deactivates_The_Plan()
    {
        // Arrange — the rewind is a one-shot recovery; a second ambiguous capture must not loop forever.
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Be" });
        plan.AdvanceOverSatisfied();
        var ambiguous = $"{{\"Groups\":[{{\"Id\":\"{GroupGuid}\"}},{{\"Id\":\"{ClientGuid}\"}}],\"TotalCount\":2}}";

        // Act — first ambiguous capture rewinds, user re-answers, still ambiguous
        plan.Observe([SuccessCall("list_groups", ambiguous)]);
        plan.FillSlot("groupName", "Be");
        plan.AdvanceOverSatisfied();
        plan.Observe([SuccessCall("list_groups", ambiguous)]);

        // Assert
        Assert.That(plan.IsActive, Is.False);
    }

    [Test]
    public void Rewind_Sets_CaptureRewindUsed_So_It_Can_Be_Persisted()
    {
        // Arrange
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Be" });
        plan.AdvanceOverSatisfied();

        // Act — first ambiguous capture rewinds and marks the one-shot guard spent
        plan.Observe([SuccessCall("list_groups",
            $"{{\"Groups\":[{{\"Id\":\"{GroupGuid}\"}},{{\"Id\":\"{ClientGuid}\"}}],\"TotalCount\":2}}")]);

        // Assert — the flag is exposed so Persist carries it into the pending recipe
        Assert.That(plan.CaptureRewindUsed, Is.True);
    }

    [Test]
    public void Resumed_Plan_With_Spent_Rewind_Deactivates_On_Next_Ambiguity()
    {
        // Arrange — mirrors the real flow: a prior turn spent the rewind, this turn is a FRESH plan
        // rebuilt from the pending store with captureRewindUsed restored. Without persisting the flag the
        // guard would re-arm here and the recipe would re-ask forever.
        var plan = new RecipeExecutionPlan(
            "r", AddClientToGroupSteps(),
            new Dictionary<string, string> { ["groupName"] = "Be" },
            stepIndex: 0,
            captureRewindUsed: true);
        plan.AdvanceOverSatisfied();

        // Act — the search is still ambiguous
        plan.Observe([SuccessCall("list_groups",
            $"{{\"Groups\":[{{\"Id\":\"{GroupGuid}\"}},{{\"Id\":\"{ClientGuid}\"}}],\"TotalCount\":2}}")]);

        // Assert — no second rewind; the plan deactivates
        Assert.That(plan.IsActive, Is.False);
    }

    [Test]
    public void Failed_Call_Does_Not_Advance()
    {
        // Arrange
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());
        plan.PrefillSlots(new Dictionary<string, string> { ["groupName"] = "Bern" });
        plan.AdvanceOverSatisfied();

        // Act
        plan.Observe([new LLMFunctionCall { FunctionName = "list_groups", Success = false, Result = "error" }]);

        // Assert — still on the search step, nothing captured
        Assert.That(plan.CurrentSkill, Is.EqualTo("list_groups"));
        Assert.That(plan.Slots.ContainsKey("groupId"), Is.False);
    }

    [Test]
    public void Mutate_Injects_Captured_Guids_And_Surfaces_FreeText_As_Known_Value()
    {
        // Arrange — all ids captured, validFrom is free text from the user
        var slots = new Dictionary<string, string>
        {
            ["groupId"] = GroupGuid,
            ["clientId"] = ClientGuid,
            ["validFrom"] = "ab 1. Mai"
        };
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps(), slots, stepIndex: 5);

        // Act
        var injections = plan.GetParameterInjections("add_client_to_group");
        var knownValues = plan.GetKnownValuesNote();

        // Assert — ids injected deterministically; the date is offered as a known value, not injected
        Assert.That(injections["clientId"], Is.EqualTo(ClientGuid));
        Assert.That(injections["groupId"], Is.EqualTo(GroupGuid));
        Assert.That(injections.ContainsKey("validFrom"), Is.False);
        Assert.That(knownValues, Does.Contain("validFrom = ab 1. Mai"));
    }

    [Test]
    public void Resume_Restores_Slot_Bag_And_Step_Index()
    {
        // Arrange — rebuilt from a pending pause on the clientName ask (index 2)
        var slots = new Dictionary<string, string> { ["groupName"] = "Bern", ["groupId"] = GroupGuid };

        // Act
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps(), slots, stepIndex: 2);

        // Assert
        Assert.That(plan.CurrentIsAsk, Is.True);
        Assert.That(plan.CurrentAskPrompt, Is.EqualTo("Which employee?"));
        Assert.That(plan.Slots["groupId"], Is.EqualTo(GroupGuid));
    }

    [Test]
    public void FillSlot_On_Resume_Then_Advance_Past_The_Answered_Ask()
    {
        // Arrange — paused on clientName ask, user replies "Hans"
        var slots = new Dictionary<string, string> { ["groupName"] = "Bern", ["groupId"] = GroupGuid };
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps(), slots, stepIndex: 2);

        // Act
        plan.FillSlot("clientName", "Hans");
        plan.AdvanceOverSatisfied();

        // Assert — moved on to the employee search
        Assert.That(plan.CurrentIsAsk, Is.False);
        Assert.That(plan.CurrentSkill, Is.EqualTo("search_employees"));
        Assert.That(plan.GetParameterInjections("search_employees")["searchTerm"], Is.EqualTo("Hans"));
    }

    [Test]
    public void Default_Plan_Does_Not_Need_Confirmation()
    {
        // Arrange / Act
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());

        // Assert
        Assert.That(plan.NeedsConfirmation, Is.False);
    }

    [Test]
    public void Plan_Built_With_NeedsConfirmation_Requires_ConfirmAndProceed_To_Clear_The_Flag()
    {
        // Arrange — mirrors a semantic-fallback match, which must be confirmed before it can be forced
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps(), needsConfirmation: true);

        // Act / Assert
        Assert.That(plan.NeedsConfirmation, Is.True);
        plan.ConfirmAndProceed();
        Assert.That(plan.NeedsConfirmation, Is.False);
    }

    [Test]
    public void Goal_Defaults_To_Name_When_Not_Provided()
    {
        // Arrange / Act
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps());

        // Assert — the confirmation instruction always has something to format, even for old callers
        Assert.That(plan.Goal, Is.EqualTo("r"));
    }

    [Test]
    public void Goal_Is_Carried_Separately_From_Name_When_Provided()
    {
        // Arrange / Act
        var plan = new RecipeExecutionPlan("r", AddClientToGroupSteps(), goal: "Onboard a new employee end to end.");

        // Assert
        Assert.That(plan.Goal, Is.EqualTo("Onboard a new employee end to end."));
        Assert.That(plan.Name, Is.EqualTo("r"));
    }
}
