// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RecipeEngineService's semantic fallback — verifies that a message which does
/// not match any recipe's keyword trigger can still resolve the recipe via a strong-confidence
/// KnowledgeIndex hit (Kind=Recipe), while a weak or missing hit correctly falls through to no
/// match.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceSemanticFallbackTests
{
    private IAgentRecipeRepository _recipeRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private RecipeEngineService _service = null!;

    private static readonly AgentRecipe OnboardRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "onboard-employee",
        Goal = "Onboard a new employee end to end.",
        TriggerJson = """{"allOf":[{"anyWordStart":["onboard","einstell"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
    };

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe });

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IKnowledgeRetrievalService)).Returns(_retrieval);
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static RetrievalResult RecipeResult(string sourceId, double score) =>
        new([new RetrievalCandidate(
            new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = sourceId, Text = "irrelevant" },
            score)]);

    [Test]
    public async Task MessageWithoutTriggerKeyword_ButStrongSemanticHit_ResolvesTheRecipe()
    {
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_ButStrongSemanticHit_PlanNeedsConfirmation()
    {
        // A recipe matched purely by meaning (no explicit trigger keyword) must not start its flow
        // directly — the chat loop gates it behind a confirmation question (expensive false positives).
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeTrue();
        plan.Goal.ShouldBe(OnboardRecipe.Goal);
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_WeakSemanticHit_DoesNotResolve()
    {
        var message = "Irgendwas ganz anderes, das mit keinem Rezept zu tun hat.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.2));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_GreyZoneHit_ResolvesButNeedsConfirmation()
    {
        // A cross-lingual query can land in the grey zone (0.4..0.7) against the de/en embedding text.
        // It must still surface the recipe, but only behind the confirmation gate — never start directly.
        var message = "Neuen Mitarbeiter, bitte.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.55));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_BelowGreyZone_DoesNotResolve()
    {
        var message = "Nachricht knapp unter der Grauzone.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.39));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_TwoCloseCandidates_SurfacesAlternativeInConfirmation()
    {
        // Two recipes match almost equally: the confirmation question must offer both interpretations
        // instead of the engine silently railroading the user into the top pick.
        var groupRecipe = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "create-group",
            Goal = "Create a new group.",
            TriggerJson = """{"allOf":[{"anyWordStart":["gruppe"]}],"noneOf":[]}""",
            StepsJson = """[{"kind":"mutate","skill":"create_group"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe, groupRecipe });

        var message = "Etwas Neues anlegen.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "onboard-employee", Text = "x" }, 0.72),
                new RetrievalCandidate(new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "create-group", Text = "x" }, 0.70),
            ]));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
        plan.NeedsConfirmation.ShouldBeTrue();
        plan.AlternativeGoal.ShouldBe("Create a new group.");
        plan.ConfirmationInstruction.ShouldContain("Create a new group.");
        plan.ConfirmationInstruction.ShouldContain(OnboardRecipe.Goal);
        plan.ConfirmationInstruction.ShouldNotContain("yes/no");
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_SecondCandidateOutsideMargin_NoAlternative()
    {
        var groupRecipe = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "create-group",
            Goal = "Create a new group.",
            TriggerJson = """{"allOf":[{"anyWordStart":["gruppe"]}],"noneOf":[]}""",
            StepsJson = """[{"kind":"mutate","skill":"create_group"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe, groupRecipe });

        var message = "Etwas Neues anlegen.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "onboard-employee", Text = "x" }, 0.82),
                new RetrievalCandidate(new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "create-group", Text = "x" }, 0.55),
            ]));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.AlternativeGoal.ShouldBeNull();
        plan.ConfirmationInstruction.ShouldContain(OnboardRecipe.Goal);
        plan.ConfirmationInstruction.ShouldContain("yes/no");
    }

    [Test]
    public async Task MessageWithoutTriggerKeyword_NoRetrievalHits_DoesNotResolve()
    {
        var message = "Irgendwas ganz anderes, das mit keinem Rezept zu tun hat.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task LeadingDecline_NeverConsultsSemanticFallback()
    {
        // Transcript case 2026-07-11: "Nein, nein, nein..." answered the assistant's own question
        // ("Willst du mehr über einen bestimmten Bereich erfahren?") and must never be ranked
        // against the mutation recipes — even when the embedding would land two candidates in the
        // grey zone and trigger the "two possible actions" disambiguation.
        var message = "Nein, nein, nein, nein, nein, nein.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task LeadingDecline_WithMisheardTail_NeverConsultsSemanticFallback()
    {
        // STT mishearing from the same transcript ("zuhüssen" for "zuhören/wissen"): the unknown
        // tail word must not defeat the leading-negation rule.
        var message = "Nein, im Moment will ich nicht zuhüssen.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task LeadingNegation_WithMutationVerb_StillConsultsSemanticFallback()
    {
        // "Nein, erfasse stattdessen ..." corrects course instead of declining: the mutation verb
        // re-enables the semantic fallback so the guided flow stays reachable.
        var message = "Nein, erfasse stattdessen einen neuen Mitarbeiter für mich.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task MessageMatchingKeywordTrigger_NeverConsultsSemanticFallback()
    {
        var message = "Bitte onboard einen neuen Mitarbeiter";

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task MessageMatchingKeywordTrigger_PlanDoesNotNeedConfirmation()
    {
        // The deterministic fast path is precise by construction — it must keep starting the flow
        // directly, exactly as before the confirmation gate was introduced.
        var message = "Bitte onboard einen neuen Mitarbeiter";

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeFalse();
    }

    [Test]
    public async Task SemanticFallback_RetrievalThrows_IsSwallowedAndReturnsNull()
    {
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter anlegen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<RetrievalResult>(_ => throw new InvalidOperationException("boom"));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task GuaranteedSkillNamesAsync_MessageWithoutTriggerKeyword_ButStrongSemanticHit_GuaranteesStepSkills()
    {
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var skills = await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, message);

        skills.ShouldBe(["create_employee"]);
    }

    [Test]
    public async Task GuaranteedSkillNamesAsync_MessageWithoutTriggerKeyword_WeakSemanticHit_ReturnsEmpty()
    {
        var message = "Irgendwas ganz anderes, das mit keinem Rezept zu tun hat.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.2));

        var skills = await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, message);

        skills.ShouldBeEmpty();
    }

    [Test]
    public async Task GuaranteedSkillNamesAsync_MessageMatchingKeywordTrigger_NeverConsultsSemanticFallback()
    {
        var message = "Bitte onboard einen neuen Mitarbeiter";

        var skills = await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, message);

        skills.ShouldBe(["create_employee"]);
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task GuaranteedSkillNamesThenResolve_SameMessage_RunsSemanticFallbackOnlyOnce()
    {
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var skills = await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, message);
        var plan = await _service.ResolveAsync(message);

        skills.ShouldBe(["create_employee"]);
        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
        await _retrieval.ReceivedWithAnyArgs(1).RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task GuaranteedSkillNamesThenResolve_SameMessageWithoutAnyMatch_RunsSemanticFallbackOnlyOnce()
    {
        var message = "Irgendwas ganz anderes, das mit keinem Rezept zu tun hat.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        var skills = await _service.GuaranteedSkillNamesAsync(userId: null, conversationId: null, message);
        var plan = await _service.ResolveAsync(message);

        skills.ShouldBeEmpty();
        plan.ShouldBeNull();
        await _retrieval.ReceivedWithAnyArgs(1).RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task ReadQuestion_TopicallyNearMutationRecipe_DoesNotConsultSemanticFallback()
    {
        // "Which groups exist?" scores 0.6+ against create-group by topic, but it is a READ, not an
        // action. The mutation-intent gate must keep the semantic fallback from hijacking the turn into
        // a recipe confirmation. The stubbed strong hit proves the gate — not a weak score — blocks it.
        var message = "Welche Gruppen gibt es bei uns?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.82));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task VerblessActionRequest_NotAQuestion_StillConsultsSemanticFallback()
    {
        // "New employee, please" carries no mutation verb but is a genuine action request — the negative
        // question-gate must let it reach the guided flow (positive mutation-signal gating would starve
        // exactly these terse phrasings). Counterpart to the read-question suppression above.
        var message = "Neuen Mitarbeiter, bitte.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(RecipeResult("onboard-employee", 0.64));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe("onboard-employee");
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task NoEnabledRecipes_NeverConsultsSemanticFallback()
    {
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var message = "Irgendeine Nachricht ohne jeden Rezept-Bezug.";

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task ResumeAsync_PendingAwaitingConfirmation_RebuildsPlanStillNeedingConfirmation()
    {
        // A pause persisted while the confirmation gate is open must resume in the same state — the
        // gate is not implicitly cleared by a mere resume (only an affirmation in LLMService clears it).
        var userId = Guid.NewGuid();
        const string conversationId = "conv-1";
        _recipeRepository.GetByNameAsync("onboard-employee", Arg.Any<CancellationToken>())
            .Returns(OnboardRecipe);
        _pendingRecipeStore.Peek(userId, conversationId).Returns(new PendingRecipe
        {
            UserId = userId,
            ConversationId = conversationId,
            RecipeName = "onboard-employee",
            AwaitingConfirmation = true,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

        var resumed = await _service.ResumeAsync(userId, conversationId);

        resumed.ShouldNotBeNull();
        resumed!.NeedsConfirmation.ShouldBeTrue();
        resumed.Goal.ShouldBe(OnboardRecipe.Goal);
    }

    [Test]
    public async Task Persist_AfterConfirmAndProceed_SavesAwaitingConfirmationAsFalse()
    {
        // Persist must reflect the plan's current NeedsConfirmation, not just always carry the flag
        // from before the gate was cleared — otherwise a confirmed recipe would re-ask forever.
        var userId = Guid.NewGuid();
        const string conversationId = "conv-1";
        var plan = new RecipeExecutionPlan(
            "onboard-employee",
            new List<RecipeStep>
            {
                new() { Kind = RecipeStepKinds.Mutate, Skill = "create_employee" }
            },
            needsConfirmation: true);
        plan.ConfirmAndProceed();

        _service.Persist(userId, conversationId, plan);

        _pendingRecipeStore.Received(1).Save(Arg.Is<PendingRecipe>(p => p.AwaitingConfirmation == false));
    }
}
