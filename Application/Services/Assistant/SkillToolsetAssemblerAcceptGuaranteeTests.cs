// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proposal (needs owner decision, narrows the 2026-09-30 rule that only list_scenarios is guaranteed): with a first
/// message that shares no vocabulary with the wizard skills ("Plane für Winterthur die Woche ..."), "Ja, übernimm es."
/// did not retrieve accept_scenario. accept_scenario is guaranteed only when the previous turn showed a scenario
/// (list_scenarios, evaluate_scenario) and the reply opens with an affirmation; reject_scenario never.
/// Belongs to Klacks.Api branch feat/klacksy-accept-guarantee-proposal.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerAcceptGuaranteeTests
{
    private const string AlwaysOnSkillName = "navigate_to";
    private const string StartAutoWizardSkillName = "start_autowizard";
    private const string CreateShiftSkillName = "create_shift";
    private const string AddShiftToGroupSkillName = "add_shift_to_group";
    private const string ScheduleRoute = "/workplace/schedule";
    private const string AcceptReply = "Ja, übernimm es.";
    private const int FillerSkillCount = 40;
    private const int SmallCap = 6;

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IScenarioActivityProbe _scenarioProbe = null!;
    private Agent _agent = null!;
    private List<AgentSkill> _skills = null!;

    [SetUp]
    public void SetUp()
    {
        _skillCache = Substitute.For<ISkillCacheService>();
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _scenarioProbe = Substitute.For<IScenarioActivityProbe>();
        _scenarioProbe.HasVisibleActivityAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _agent = new Agent { Id = Guid.NewGuid() };

        _skills =
        [
            CreateSkill(AlwaysOnSkillName, SkillEffect.Read, alwaysOn: true),
            CreateSkill(CreateShiftSkillName, SkillEffect.Mutate),
            CreateSkill(AddShiftToGroupSkillName, SkillEffect.Mutate),
            CreateSkill(ScenarioFollowUpSkills.AcceptScenarioSkillName, SkillEffect.Mutate),
            CreateSkill(ScenarioFollowUpSkills.RejectScenarioSkillName, SkillEffect.Mutate),
            CreateSkill(ScenarioFollowUpSkills.ListScenariosSkillName, SkillEffect.Read),
            CreateSkill(StartAutoWizardSkillName, SkillEffect.Mutate)
        ];
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(_ => _skills);
        GivenRetrieval();
    }

    [Test]
    public async Task AnAffirmation_AfterAPresentedScenario_KeepsAcceptScenarioAndListScenarios()
    {
        var result = await AssembleAsync(
            AcceptReply, ScheduleRoute,
            previousTurnSkillNames: [ScenarioFollowUpSkills.ListScenariosSkillName, AlwaysOnSkillName]);

        var names = Names(result);
        names.ShouldContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
        names.ShouldContain(ScenarioFollowUpSkills.ListScenariosSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.RejectScenarioSkillName);
    }

    [TestCase(ScenarioFollowUpSkills.ListScenariosSkillName)]
    [TestCase(ScenarioFollowUpSkills.EvaluateScenarioSkillName)]
    public async Task EveryScenarioShowingSkill_OpensTheAcceptWindow(string previousSkill)
    {
        var result = await AssembleAsync(AcceptReply, route: null, previousTurnSkillNames: [previousSkill]);

        Names(result).ShouldContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
    }

    [TestCase(StartAutoWizardSkillName)]
    [TestCase("start_wizard1")]
    [TestCase("start_wizard2")]
    [TestCase("start_wizard3")]
    public async Task AWizardStart_DoesNotOpenTheAcceptWindow_BecauseItsScenarioDoesNotExistYet(string previousSkill)
    {
        var result = await AssembleAsync(AcceptReply, route: null, previousTurnSkillNames: [previousSkill]);

        Names(result).ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
    }

    [Test]
    public async Task AnAffirmation_WithoutAScenarioPresentingPreviousTurn_OffersNoAccept()
    {
        var result = await AssembleAsync(AcceptReply, ScheduleRoute, previousTurnSkillNames: null);

        Names(result).ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
    }

    [Test]
    public async Task AnAffirmation_AfterAnUnrelatedPreviousTurn_OffersNoAccept()
    {
        var result = await AssembleAsync(
            AcceptReply, ScheduleRoute, previousTurnSkillNames: [CreateShiftSkillName, AlwaysOnSkillName]);

        Names(result).ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
    }

    [TestCase("Zeig mir das Ergebnis des Assistenten.")]
    [TestCase("Nein, verwerfe es.")]
    [TestCase("Ja, aber nicht heute.")]
    [TestCase("Ja? Was passiert dann?")]
    [TestCase("Übernimm es.")]
    public async Task AReplyThatDoesNotOpenWithAnAffirmation_OffersNoAccept(string message)
    {
        var result = await AssembleAsync(
            message, ScheduleRoute, previousTurnSkillNames: [ScenarioFollowUpSkills.ListScenariosSkillName]);

        Names(result).ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
    }

    [Test]
    public async Task WithoutTheRightToAcceptScenarios_TheAffirmationOffersNoAccept()
    {
        _skills.Single(s => s.Name == ScenarioFollowUpSkills.AcceptScenarioSkillName).RequiredPermission = "CanEditSchedule";

        var result = await AssembleAsync(
            AcceptReply, ScheduleRoute, previousTurnSkillNames: [ScenarioFollowUpSkills.ListScenariosSkillName]);

        Names(result).ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
    }

    [Test]
    public async Task AcceptScenario_SurvivesTheProviderCap_AgainstAFullPoolOfRetrievedSkills()
    {
        var fillers = AddFillerSkills();
        GivenRetrieval(fillers.ToArray());

        var result = await AssembleAsync(
            AcceptReply, ScheduleRoute, maxTools: SmallCap,
            previousTurnSkillNames: [ScenarioFollowUpSkills.ListScenariosSkillName]);

        var names = Names(result);
        names.ShouldContain(AlwaysOnSkillName);
        names.ShouldContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
        names.ShouldContain(ScenarioFollowUpSkills.ListScenariosSkillName);
    }

    private List<string> AddFillerSkills()
    {
        var fillers = Enumerable.Range(0, FillerSkillCount).Select(index => $"filler_skill_{index:D2}").ToList();
        _skills.AddRange(fillers.Select(name => CreateSkill(name, SkillEffect.Mutate)));
        return fillers;
    }

    private async Task<SkillToolsetResult> AssembleAsync(
        string message,
        string? route,
        int maxTools = 30,
        IReadOnlyList<string>? previousTurnSkillNames = null)
    {
        var resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            CreateRecipeEngine(),
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(),
            CreatePhrases(),
            Substitute.For<ISkillRelationRepository>(),
            new NoTurnIntentExplainGuarantee(),
            _scenarioProbe,
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());

        var queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string>(0));
        var expander = Substitute.For<ISkillRetrievalExpander>();
        expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        var assembler = new SkillToolsetAssembler(
            _skillCache, _retrieval, queryBuilder, expander, resolver,
            Substitute.For<ILogger<SkillToolsetAssembler>>());

        return await assembler.AssembleAsync(
            _agent, new List<string>(), message, "conv-retrieval-gap", route, Guid.NewGuid().ToString(),
            "de", maxTools, applyLearnedPhraseGuarantee: true, excludedSkillNames: null, pinnedSkillNames: null,
            previousTurnSkillNames, continuationSkillNames: null, CancellationToken.None);
    }

    private static RecipeEngineService CreateRecipeEngine()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepositoryStub = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepositoryStub);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        return new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static ISkillPhraseRepository CreatePhrases()
    {
        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());
        return phrases;
    }

    private void GivenRetrieval(params string[] skillNames)
    {
        var candidates = skillNames
            .Select(name => new RetrievalCandidate(
                new KnowledgeEntry
                {
                    Id = Guid.NewGuid(),
                    Kind = KnowledgeEntryKind.Skill,
                    SourceId = name,
                    Text = $"{name}. A skill."
                },
                0.9))
            .ToList();

        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(candidates));
    }

    private static List<string> Names(SkillToolsetResult result) =>
        result.Functions.Select(f => f.Name).ToList();

    private static AgentSkill CreateSkill(string name, SkillEffect effect, bool alwaysOn = false) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = effect,
        AlwaysOn = alwaysOn,
        TriggerKeywords = "[]"
    };
}