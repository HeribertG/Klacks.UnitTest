// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// MailApplyGuarantee wired through SkillToolsetGuaranteeResolver: when the user asks to enter what the e-mails contain, the
/// person lookup, the availability write, both planning-command writes and the schedule view are kept as hints, whatever
/// retrieval ranked. Live 2026-11-04: the toolset hit the cap of 30 with mail-management skills and no write skill, and the
/// model answered that it could not enter availabilities. Without the mail context, or for a question, nothing is added.
/// ScheduleOverlayGuarantee rides in the same fixture: a request for the schedule with the availabilities or keywords shown
/// keeps open_schedule as a hint.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetGuaranteeResolverMailApplyTests
{
    private const string ConversationId = "conv-mail-apply";
    private const string EnterEverything =
        "Trag das bitte alles ein: Verfügbarkeiten zu den Verfügbarkeiten, Wünsche als Keywords in den Plan.";
    private const string QuestionAboutMails = "Was steht in den neuen E-Mails vom Team?";

    private static readonly string[] EntrySkills =
    [
        "search_employees", "set_client_availability", "add_schedule_command", "add_schedule_commands_range", "open_schedule"
    ];

    private static readonly AgentRecipe AvailabilityRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "record-availability-for-employee",
        Goal = "Record the availability of an employee.",
        TriggerJson = """{"allOf":[{"anyWordStart":["trag"]},{"anySubstring":["verfügbar"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"recipe_only_step"}]""",
        IsEnabled = true,
        SortOrder = 1
    };

    private SkillToolsetGuaranteeResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        var relations = Substitute.For<ISkillRelationRepository>();
        relations.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { AvailabilityRecipe });
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepository = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepository);
        var retriever = Substitute.For<IRecipeCandidateRetriever>();
        retriever.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new List<RecipeCandidate>());
        scopedProvider.GetService(typeof(IRecipeCandidateRetriever)).Returns(retriever);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        var recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());

        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());
        var scenarioProbe = Substitute.For<IScenarioActivityProbe>();
        scenarioProbe.HasVisibleActivityAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        _resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(),
            phrases,
            relations,
            new NoTurnIntentExplainGuarantee(),
            scenarioProbe,
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());
    }

    [TearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
        DeclineDetector.Reset();
    }

    [Test]
    public async Task EnterEverything_AfterTheMailsWereRead_GuaranteesTheEntrySkillsAsHints()
    {
        var result = await Resolve(EnterEverything, previousTurn: ["list_emails", "read_email"]);

        foreach (var skill in EntrySkills)
        {
            Names(result).ShouldContain(skill);
            result.GuaranteedSources[skill].ShouldBe(ToolsetSkillSource.Hint);
        }
    }

    [Test]
    public async Task EnterEverything_AfterTheMailsWereRead_DoesNotLetTheKeywordRecipeClaimItsOwnSteps()
    {
        var result = await Resolve(EnterEverything, previousTurn: ["read_email"], extraSkills: ["recipe_only_step"]);

        Names(result).ShouldContain("set_client_availability");
        Names(result).ShouldNotContain("recipe_only_step");
    }

    [Test]
    public async Task TheSameSentence_WithoutAMailRead_KeepsTheKeywordRecipeGuarantee_AndNoEntrySkills()
    {
        var result = await Resolve(EnterEverything, previousTurn: null, extraSkills: ["recipe_only_step"]);

        Names(result).ShouldContain("recipe_only_step");
        Names(result).ShouldNotContain("add_schedule_commands_range");
        Names(result).ShouldNotContain("open_schedule");
    }

    [Test]
    public async Task AQuestionAboutTheMails_GuaranteesNoEntrySkill()
    {
        var result = await Resolve(QuestionAboutMails, previousTurn: ["read_email"]);

        Names(result).ShouldNotContain("set_client_availability");
        Names(result).ShouldNotContain("add_schedule_commands_range");
    }

    [Test]
    public async Task AnEntrySkillTheUserMayNotUse_IsNotGuaranteed()
    {
        var result = await Resolve(
            EnterEverything, previousTurn: ["read_email"], skillsToOmit: ["add_schedule_commands_range"]);

        Names(result).ShouldNotContain("add_schedule_commands_range");
        Names(result).ShouldContain("add_schedule_command");
    }

    [Test]
    public async Task ReadAndEnterInOneMessage_AlsoGuaranteesTheMailReadingSkills()
    {
        var result = await Resolve(
            "Lies die Mails vom Team und trag die Verfügbarkeiten ein.", previousTurn: null,
            extraSkills: ["list_emails", "read_email"]);

        Names(result).ShouldContain("read_email");
        Names(result).ShouldContain("list_emails");
        Names(result).ShouldContain("set_client_availability");
    }

    [Test]
    public async Task EnterAfterTheMailsWereRead_DoesNotSpendSlotsOnTheMailReadingSkills()
    {
        var result = await Resolve(EnterEverything, previousTurn: ["read_email"], extraSkills: ["list_emails", "read_email"]);

        Names(result).ShouldNotContain("read_email");
        Names(result).ShouldNotContain("list_emails");
    }

    [Test]
    public async Task ShowTheMonthWithAvailabilitiesAndKeywords_GuaranteesOpenScheduleAsHint_WithoutAnyMailRead()
    {
        var result = await Resolve("Zeig mir den Dezember mit Verfügbarkeiten und Keywords", previousTurn: null);

        Names(result).ShouldContain("open_schedule");
        result.GuaranteedSources["open_schedule"].ShouldBe(ToolsetSkillSource.Hint);
        Names(result).ShouldNotContain("set_client_availability");
    }

    [Test]
    public async Task ShowTheAvailabilitiesAlone_GuaranteesNoOpenSchedule()
    {
        var result = await Resolve("Zeig mir die Verfügbarkeiten von Mia", previousTurn: null);

        Names(result).ShouldNotContain("open_schedule");
    }

    private Task<SkillToolsetGuaranteeResult> Resolve(
        string message,
        IReadOnlyList<string>? previousTurn,
        string[]? extraSkills = null,
        string[]? skillsToOmit = null)
    {
        var skills = EntrySkills
            .Concat(extraSkills ?? [])
            .Where(name => skillsToOmit == null || !skillsToOmit.Contains(name))
            .Select(Skill)
            .ToList();
        var request = new SkillToolsetGuaranteeRequest(
            Guid.NewGuid(), skills, [], message, ConversationId, null, Guid.NewGuid().ToString(), "de", [],
            null, previousTurn, null, true);

        return _resolver.ResolveAsync(request, CancellationToken.None);
    }

    private static AgentSkill Skill(string name) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = SkillEffect.Mutate,
        TriggerKeywords = "[]",
    };

    private static List<string> Names(SkillToolsetGuaranteeResult result) =>
        result.GuaranteedSkills.Select(s => s.Name).ToList();
}
