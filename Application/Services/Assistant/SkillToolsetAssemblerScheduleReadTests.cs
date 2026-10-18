// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Schedule-read guarantee of the shared SkillToolsetAssembler: a dated duty or plan question and the schedule page
/// keep read_schedule_state in the toolset although vector retrieval never ranks it for such wording. A recognised
/// question also keeps list_groups (the skill needs a group id); the page alone keeps only read_schedule_state.
/// list_groups is the weakest guarantee and goes first at a small provider cap. The skills survive the cap, stay out
/// when their signal is missing, and never pull in the scenario accept/reject/list skills on their own. Since the owner
/// approval of 2026-10-16 accept_scenario may be guaranteed, but only for an affirmation right after a turn that showed
/// scenarios; reject_scenario is never guaranteed.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
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
public class SkillToolsetAssemblerScheduleReadTests
{
    private const string AlwaysOnSkillName = "navigate_to";
    private const string CreateShiftSkillName = "create_shift";
    private const string AddShiftToGroupSkillName = "add_shift_to_group";
    private const string ExplainPageScheduleSkillName = "explain_page_schedule";
    private const string ExtraHintSkillName = "extra_hint_skill";
    private const string AlwaysOnFillerPrefix = "always_on_filler_";
    private const int AlwaysOnFillerCount = 8;
    private const int TinyCap = 12;
    private const string ScheduleRoute = "/workplace/schedule";
    private const string DashboardRoute = "/workplace/dashboard";
    private const string DutyQuestion = "Wer hat am Montag, 19. Oktober, in Winterthur Dienst?";
    private const string PlanQuestion = "Was steht am Montag, 19. Oktober, in Winterthur an?";
    private const string AcceptReply = "Ja, übernimm es.";
    private const string WhyNotQuestion = "Warum kann Anna am Montag, 19. Oktober, den Frühdienst nicht bekommen?";
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
            CreateSkill(ScheduleReadIntentResolver.ReadScheduleStateSkillName, SkillEffect.Read),
            CreateSkill(ScheduleReadIntentResolver.ListGroupsSkillName, SkillEffect.Read),
            CreateSkill(CreateShiftSkillName, SkillEffect.Mutate),
            CreateSkill(AddShiftToGroupSkillName, SkillEffect.Mutate),
            CreateSkill(ExplainPageScheduleSkillName, SkillEffect.Explain),
            CreateSkill(ExtraHintSkillName, SkillEffect.Read),
            CreateSkill(ScenarioFollowUpSkills.AcceptScenarioSkillName, SkillEffect.Mutate),
            CreateSkill(ScenarioFollowUpSkills.RejectScenarioSkillName, SkillEffect.Mutate),
            CreateSkill(ScenarioFollowUpSkills.ListScenariosSkillName, SkillEffect.Read),
            CreateSkill(SkillNames.DiagnoseWorkBooking, SkillEffect.Read),
            CreateSkill(SkillNames.ExplainWhyCannotBook, SkillEffect.Explain)
        ];
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(_ => _skills);
        GivenRetrieval();
    }

    [TestCase(DutyQuestion)]
    [TestCase(PlanQuestion)]
    [TestCase("Who is working on Monday, October 19 in Winterthur?")]
    public async Task ADatedDutyQuestion_KeepsReadScheduleState_WhenRetrievalOnlyFindsShiftStructureSkills(string message)
    {
        GivenRetrieval(CreateShiftSkillName, AddShiftToGroupSkillName);

        var result = await AssembleAsync(message, route: null);

        var names = Names(result);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldContain(ScheduleReadIntentResolver.ListGroupsSkillName);
    }

    [Test]
    public async Task OnTheSchedulePage_ReadScheduleStateIsKept_ForAnyMessage_WithoutListGroups()
    {
        var result = await AssembleAsync("Kto pracuje w poniedziałek?", ScheduleRoute);

        var names = Names(result);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldNotContain(ScheduleReadIntentResolver.ListGroupsSkillName);
    }

    [Test]
    public async Task OnTheSchedulePage_ADutyQuestion_KeepsBothSkills_AndNoScenarioSkill()
    {
        var result = await AssembleAsync(DutyQuestion, ScheduleRoute);

        var names = Names(result);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldContain(ScheduleReadIntentResolver.ListGroupsSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.RejectScenarioSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.ListScenariosSkillName);
    }

    [Test]
    public async Task OnTheSchedulePage_ADutyQuestion_AfterShownScenarios_OffersNoAcceptAndNoReject()
    {
        var result = await AssembleAsync(
            DutyQuestion, ScheduleRoute, previousTurnSkillNames: [ScenarioFollowUpSkills.ListScenariosSkillName]);

        var names = Names(result);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.RejectScenarioSkillName);
    }

    [Test]
    public async Task OnTheSchedulePage_AnAffirmation_AfterShownScenarios_KeepsAcceptButNeverReject()
    {
        var result = await AssembleAsync(
            AcceptReply, ScheduleRoute, previousTurnSkillNames: [ScenarioFollowUpSkills.ListScenariosSkillName]);

        var names = Names(result);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
        names.ShouldContain(ScenarioFollowUpSkills.ListScenariosSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.RejectScenarioSkillName);
    }

    [Test]
    public async Task OnTheSchedulePage_AnAffirmation_WithoutShownScenarios_OffersNoScenarioSkill()
    {
        var result = await AssembleAsync(AcceptReply, ScheduleRoute);

        var names = Names(result);
        names.ShouldNotContain(ScenarioFollowUpSkills.AcceptScenarioSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.RejectScenarioSkillName);
        names.ShouldNotContain(ScenarioFollowUpSkills.ListScenariosSkillName);
    }

    [Test]
    public async Task AtTheTinyCap_ListGroupsIsCutBeforeReadScheduleStateAndOtherHints()
    {
        _skills.AddRange(Enumerable.Range(0, AlwaysOnFillerCount)
            .Select(index => CreateSkill($"{AlwaysOnFillerPrefix}{index}", SkillEffect.Read, alwaysOn: true)));

        var result = await AssembleAsync(
            DutyQuestion, ScheduleRoute, maxTools: TinyCap, pinnedSkillNames: [ExtraHintSkillName]);

        var names = Names(result);
        names.Count.ShouldBe(TinyCap);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldContain(ExplainPageScheduleSkillName);
        names.ShouldContain(ExtraHintSkillName);
        names.ShouldNotContain(ScheduleReadIntentResolver.ListGroupsSkillName);
    }

    [Test]
    public async Task AtTheTinyCap_AWhyNotQuestionKeepsTheDiagnosis_AndCutsTheExplanationAndListGroupsFirst()
    {
        _skills.AddRange(Enumerable.Range(0, AlwaysOnFillerCount)
            .Select(index => CreateSkill($"{AlwaysOnFillerPrefix}{index}", SkillEffect.Read, alwaysOn: true)));

        var result = await AssembleAsync(WhyNotQuestion, ScheduleRoute, maxTools: TinyCap);

        var names = Names(result);
        names.Count.ShouldBe(TinyCap);
        names.ShouldContain(SkillNames.DiagnoseWorkBooking);
        names.ShouldContain(ExplainPageScheduleSkillName);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldNotContain(SkillNames.ExplainWhyCannotBook);
        names.ShouldNotContain(ScheduleReadIntentResolver.ListGroupsSkillName);
    }

    [TestCase("Warum kann Anna am Montag den Frühdienst nicht bekommen?", null)]
    [TestCase("Why can't Anna get this shift?", ScheduleRoute)]
    public async Task AWhyNotBookingQuestion_KeepsTheDiagnosisAndTheExplanation_WhenRetrievalFindsNeither(
        string message, string? route)
    {
        GivenRetrieval(CreateShiftSkillName, AddShiftToGroupSkillName);

        var names = Names(await AssembleAsync(message, route));

        names.ShouldContain(SkillNames.DiagnoseWorkBooking);
        names.ShouldContain(SkillNames.ExplainWhyCannotBook);
    }

    [Test]
    public async Task ADatedDutyQuestionWithoutNegation_DoesNotForceTheBookingDiagnosis()
    {
        var names = Names(await AssembleAsync(DutyQuestion, ScheduleRoute));

        names.ShouldNotContain(SkillNames.DiagnoseWorkBooking);
        names.ShouldNotContain(SkillNames.ExplainWhyCannotBook);
    }

    [Test]
    public async Task OnAnotherPage_WithoutADatedDutyQuestion_ReadScheduleStateIsNotForced()
    {
        var result = await AssembleAsync("Danke, das war alles.", DashboardRoute);

        var names = Names(result);
        names.ShouldNotContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldNotContain(ScheduleReadIntentResolver.ListGroupsSkillName);
    }

    [Test]
    public async Task ADutyStatement_WithoutAQuestion_DoesNotForceReadScheduleState()
    {
        var result = await AssembleAsync("Erstelle am Montag, 19. Oktober, einen Dienst in Winterthur", route: null);

        Names(result).ShouldNotContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
    }

    [Test]
    public async Task WithoutTheRightToTheSkill_ItIsNotOffered()
    {
        _skills.Single(s => s.Name == ScheduleReadIntentResolver.ReadScheduleStateSkillName).RequiredPermission = "CanViewSchedule";

        var result = await AssembleAsync(DutyQuestion, ScheduleRoute);

        Names(result).ShouldNotContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
    }

    [Test]
    public async Task ReadScheduleState_SurvivesTheProviderCap_AgainstAFullPoolOfRetrievedSkills()
    {
        var fillers = AddFillerSkills();
        GivenRetrieval(fillers.ToArray());

        var result = await AssembleAsync(DutyQuestion, ScheduleRoute, maxTools: SmallCap);

        var names = Names(result);
        names.Count.ShouldBe(SmallCap);
        names.ShouldContain(AlwaysOnSkillName);
        names.ShouldContain(ScheduleReadIntentResolver.ReadScheduleStateSkillName);
        names.ShouldContain(ScheduleReadIntentResolver.ListGroupsSkillName);
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
        IReadOnlyCollection<string>? pinnedSkillNames = null,
        IReadOnlyList<string>? previousTurnSkillNames = null)
    {
        var resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            CreateRecipeEngine(),
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(),
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
            "de", maxTools, applyLearnedPhraseGuarantee: true, excludedSkillNames: null, pinnedSkillNames,
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
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
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