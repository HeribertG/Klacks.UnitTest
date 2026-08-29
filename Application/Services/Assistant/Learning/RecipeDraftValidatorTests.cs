// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the gate in front of every learned capability. It matters more than its size suggests: the
/// quality gates that keep hand-written recipes disjoint read recipe-seeds.json, so a recipe written
/// straight into agent_recipes passes none of them, and a recipe forces its step skill deterministically
/// ahead of any function calling. A trigger that is one word too generic silently steals turns from a
/// skill nobody thought about.
/// The hijack rules are therefore checked in all three directions the engine can be fooled in: an
/// existing recipe's wording, a skill's own trigger phrase, and a frozen routing expectation.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class RecipeDraftValidatorTests
{
    private ISkillRegistry _registry = null!;
    private RecipeDraftValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _registry.GetAllSkills().Returns([]);

        _validator = new RecipeDraftValidator(
            _registry, Substitute.For<ILogger<RecipeDraftValidator>>());
    }

    [Test]
    public void AWellFormedDraft_IsAccepted()
    {
        var verdict = _validator.Validate(Draft(), [], []);

        verdict.IsAccepted.ShouldBeTrue();
        verdict.Error.ShouldBeNull();
    }

    // The learned name space is what keeps the seed loader from ever overwriting a learned recipe, so
    // the prefix is applied here rather than trusted to the generator.
    [Test]
    public void TheAcceptedName_CarriesTheLearnedPrefix()
    {
        var verdict = _validator.Validate(Draft(), [], []);

        verdict.Name.ShouldBe(SkillLearningDefaults.LearnedRecipeNamePrefix + "open-shift-report");
    }

    [TestCase("Open Shift Report")]
    [TestCase("offene_dienste")]
    [TestCase("OpenShiftReport")]
    [TestCase("-leading-dash")]
    public void ANameThatIsNotAnEnglishKebabSlug_IsRejected(string name)
    {
        var verdict = _validator.Validate(Draft(name: name), [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("kebab-case");
    }

    [Test]
    public void ADraftMissingACoreLanguageGoal_IsRejected()
    {
        var draft = Draft();
        var translations = new Dictionary<string, string>(draft.GoalTranslations);
        translations.Remove("fr");

        var verdict = _validator.Validate(draft with { GoalTranslations = translations }, [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("fr");
    }

    // Below four characters a stem matches unrelated words at a word boundary far more often than the
    // intent it was written for - the lesson from the sprachneutral-merge incident.
    [Test]
    public void ATriggerStemShorterThanFourCharacters_IsRejected()
    {
        var verdict = _validator.Validate(Draft(stems: ["add"]), [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("shorter than");
    }

    [Test]
    public void ADraftWithoutAnyTriggerCondition_IsRejected()
    {
        var draft = Draft() with { Trigger = new RecipeTrigger() };

        var verdict = _validator.Validate(draft, [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("allOf");
    }

    // A slot reference is the whole inject value or nothing: "$currentMonth-01" makes the runtime look
    // up a slot literally called "currentMonth-01", which nothing captures, so the parameter is dropped
    // without a word.
    [TestCase("$currentMonth-01")]
    [TestCase("$a-$b")]
    [TestCase("prefix$slot")]
    public void AnInjectValueJoiningASlotReferenceToOtherText_IsRejected(string value)
    {
        var draft = Draft(inject: new Dictionary<string, string> { ["month"] = value });

        var verdict = _validator.Validate(draft, [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("A slot reference is the whole value");
        verdict.Error.ShouldContain(value);
    }

    // Without the prefix this is not a slot reference at all: the runtime treats it as a literal
    // constant and the braces reach a real user's turn verbatim.
    [TestCase("{{month}}-01")]
    [TestCase("{month}")]
    public void AnInjectValueInForeignPlaceholderSyntax_IsRejected(string value)
    {
        var draft = Draft(inject: new Dictionary<string, string> { ["month"] = value });

        var verdict = _validator.Validate(draft, [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("looks like a placeholder");
        verdict.Error.ShouldContain(value);
    }

    [TestCase("$clientId")]
    [TestCase("Customer")]
    [TestCase("september 2026")]
    public void AnInjectValueThatIsAWholeSlotReferenceOrPlainLiteral_IsAccepted(string value)
    {
        var draft = Draft(inject: new Dictionary<string, string> { ["client"] = value });

        var verdict = _validator.Validate(draft, [], []);

        verdict.IsAccepted.ShouldBeTrue(verdict.Error);
    }

    // ExtractCapture silently yields nothing for any other spelling, and a capture that yields nothing
    // deactivates the whole recipe at runtime.
    [TestCase("result.id as month")]
    [TestCase("items[].id")]
    [TestCase("items as month")]
    public void ACaptureThatIsNotAnArrayFieldSlotSpec_IsRejected(string capture)
    {
        var draft = Draft(capture: capture);

        var verdict = _validator.Validate(draft, [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("A capture reads one field out of a list");
        verdict.Error.ShouldContain(capture);
    }

    [Test]
    public void ACaptureSpelledArrayFieldAsSlot_IsAccepted()
    {
        var draft = Draft(capture: "items[].id as itemId");

        var verdict = _validator.Validate(draft, [], []);

        verdict.IsAccepted.ShouldBeTrue(verdict.Error);
    }

    // A capability that WRITES must not fire on a plain question - that is the guided-mutation rule every
    // hand-written recipe follows, and it is written in rather than demanded from the generator.
    [Test]
    public void AWritingCapability_GetsTheQuestionGuard()
    {
        var verdict = _validator.Validate(Draft(mutates: true), [], []);

        verdict.IsAccepted.ShouldBeTrue(verdict.Error);
        verdict.Trigger!.NoneOf.ShouldNotBeEmpty();
        RecipeTriggerMatcher.Matches(verdict.Trigger, "Wann laufen die offenen Dienste?").ShouldBeFalse();
        RecipeTriggerMatcher.Matches(verdict.Trigger, "Welche offenen Dienste gibt es?").ShouldBeFalse();
    }

    // A read-only capability is the opposite case: answering a question IS its purpose, and most such
    // wishes are phrased as one. Applied there the guard is self-defeating - the trigger would carry
    // "welche" in allOf and in noneOf at once, which no utterance can satisfy. Two capabilities passed
    // every other gate and executed all their steps for real before the live engine refused to resolve
    // them for exactly this reason.
    [Test]
    public void AReadOnlyCapability_KeepsAnsweringQuestions()
    {
        var verdict = _validator.Validate(Draft(), [], []);

        verdict.IsAccepted.ShouldBeTrue(verdict.Error);
        verdict.Trigger!.NoneOf.ShouldBeEmpty();

        // IsVetoed rather than Matches: whether the allOf conditions happen to be satisfied by this
        // sentence is beside the point. What must hold is that a question-leading utterance is not
        // rejected out of hand, which is exactly what the guard would do.
        RecipeTriggerMatcher.IsVetoed(verdict.Trigger, "Welche offenen Dienstberichte gibt es?")
            .ShouldBeFalse();
    }

    [Test]
    public void ATriggerThatWouldAlsoFireOnAnExistingRecipesWording_IsRejected()
    {
        var existing = ExistingRecipe("create-shift-order", ["dienst", "erstell"]);

        var verdict = _validator.Validate(Draft(stems: ["dienst", "erstell"]), [existing], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("create-shift-order");
    }

    // The gap the integration fixture found against the real seed corpus. One condition of an existing
    // trigger lists several wordings of the same word; a rival built from two of those wordings matches
    // none of the sampled short sentences while colliding with the recipe completely.
    [Test]
    public void ATriggerBuiltFromTwoWordingsOfOneExistingCondition_IsRejected()
    {
        var existing = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "add-employee-to-group",
            TriggerJson = JsonSerializer.Serialize(
                new RecipeTrigger
                {
                    AllOf =
                    [
                        new RecipeCondition { AnyWordStart = ["mitarbeit", "employee"] },
                        new RecipeCondition { AnyWordStart = ["gruppe", "group"] }
                    ]
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

        var verdict = _validator.Validate(Draft(stems: ["mitarbeit", "employee"]), [existing], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("add-employee-to-group");
    }

    [Test]
    public void ATriggerThatSwallowsASkillsOwnPhrase_IsRejected()
    {
        _registry.GetAllSkills().Returns(
        [
            new SkillDescriptor(
                "start_company_rule", "Starts a company rule", SkillCategory.Action, [], [], [], null)
            {
                TriggerKeywords = ["offene dienste melden"]
            }
        ]);

        var verdict = _validator.Validate(Draft(stems: ["offene", "dienste"]), [], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("start_company_rule");
    }

    // A frozen routing expectation is a promise that this utterance keeps reaching that target. A recipe
    // runs before function calling, so swallowing the utterance breaks the promise without any
    // retrieval metric noticing.
    [Test]
    public void ATriggerThatSwallowsAFrozenRoutingExpectation_IsRejected()
    {
        var goldenCase = new SkillLearningGoldenCase
        {
            Query = "offene dienste anzeigen",
            Locale = "de",
            ExpectedSourceId = "list_open_shifts"
        };

        var verdict = _validator.Validate(Draft(stems: ["offene", "dienste"]), [], [goldenCase]);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("list_open_shifts");
    }

    [Test]
    public void ANameThatAlreadyExists_IsRejected()
    {
        var existing = ExistingRecipe(
            SkillLearningDefaults.LearnedRecipeNamePrefix + "open-shift-report", ["etwasanderes"]);

        var verdict = _validator.Validate(Draft(), [existing], []);

        verdict.IsAccepted.ShouldBeFalse();
        verdict.Error.ShouldContain("already exists");
    }

    private static AgentRecipe ExistingRecipe(string name, IReadOnlyList<string> stems) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            TriggerJson = JsonSerializer.Serialize(
                new RecipeTrigger
                {
                    AllOf = [.. stems.Select(stem => new RecipeCondition { AnyWordStart = [stem] })]
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

    private static LearnedRecipeDraft Draft(
        string name = "open-shift-report",
        IReadOnlyList<string>? stems = null,
        bool mutates = false,
        Dictionary<string, string>? inject = null,
        string? capture = null) =>
        new(
            name,
            "Report the open shifts of the coming week",
            new Dictionary<string, string>
            {
                ["de"] = "Offene Dienste der kommenden Woche melden",
                ["en"] = "Report the open shifts of the coming week",
                ["fr"] = "Signaler les services ouverts de la semaine à venir",
                ["it"] = "Segnalare i turni aperti della prossima settimana"
            },
            new RecipeTrigger
            {
                AllOf =
                [
                    .. (stems ?? ["dienstbericht", "offenmeldung"])
                        .Select(stem => new RecipeCondition { AnyWordStart = [stem] })
                ]
            },
            [
                new RecipeStep
                {
                    Kind = mutates ? RecipeStepKinds.Mutate : RecipeStepKinds.Search,
                    Skill = "list_open_shifts",
                    Inject = inject,
                    Capture = capture
                }
            ]);
}
