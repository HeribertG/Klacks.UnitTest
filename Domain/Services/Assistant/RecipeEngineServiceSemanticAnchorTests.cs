// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the lexical anchor on RecipeEngineService's semantic fallback, run against the real
/// recipe-seeds.json so a hand-copied recipe cannot drift from what ships. Regression class 2026-09-24:
/// "read my deferred notes" style messages ranked above the semantic floor against the writing recipe
/// bulk-add-externs-to-nearest-group and reached its confirmation gate. An embedding hit must now be
/// backed by the message hitting at least one non-verb allOf condition of the recipe; a language outside
/// the core set is exempt because the allOf vocabulary only exists for de/en/fr/it.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeEngineServiceSemanticAnchorTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string German = "de";
    private const string English = "en";
    private const string Spanish = "es";
    private const string ExternsRecipe = "bulk-add-externs-to-nearest-group";
    private const string OnboardRecipe = "onboard-employee";
    private const string AddToGroupRecipe = "add-employee-to-group";
    private const string BulkEmployeesToGroupRecipe = "bulk-add-employees-to-group";
    private const string BulkCustomersToNearestRecipe = "bulk-add-customers-to-nearest-group";
    private const string BulkEmployeesToNearestRecipe = "bulk-add-employees-to-nearest-group";
    private const string SelectedClientsToGroupRecipe = "add-selected-clients-to-group";
    private const string StubStepsJson = """[{"kind":"mutate","skill":"noop_skill"}]""";
    private const double StrongScore = 0.85;
    private const double GreyZoneScore = 0.55;
    private const double TopScore = 0.72;
    private const double RunnerUpScore = 0.70;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<AgentRecipe> _seededRecipes = null!;

    private IAgentRecipeRepository _recipeRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private RecipeEngineService _service = null!;

    [OneTimeSetUp]
    public void LoadSeededRecipes()
    {
        var file = LocateDefinitionsFile(RecipeSeedsFileName);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _seededRecipes = seed!.Recipes
            .OrderBy(r => r.SortOrder)
            .Select(r => new AgentRecipe
            {
                Id = Guid.NewGuid(),
                Name = r.Name,
                Goal = r.Goal,
                TriggerJson = JsonSerializer.Serialize(r.Trigger),
                StepsJson = StubStepsJson,
                IsEnabled = true,
                SortOrder = r.SortOrder
            })
            .ToList();
    }

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(_seededRecipes);

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IKnowledgeRetrievalService)).Returns(_retrieval);
        provider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private void StubRetrieval(string message, params (string SourceId, double Score)[] candidates) =>
        _retrieval.RetrieveAsync(
                message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult(candidates
                .Select(c => new RetrievalCandidate(
                    new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = c.SourceId, Text = "irrelevant" },
                    c.Score))
                .ToList()));

    private string SeededGoal(string recipeName) => _seededRecipes.Single(r => r.Name == recipeName).Goal;

    [Test]
    [TestCase("Lies bitte meine zurückgestellten Notizen vor.")]
    [TestCase("Hast du noch offene Hinweise für mich?")]
    [TestCase("Zurückgestellte Notizen verwalten, bitte.")]
    public async Task DeferredNotesMessage_StronglyRankedAgainstTheExternsRecipe_DoesNotResolve(string message)
    {
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldBeNull();
        await _retrieval.ReceivedWithAnyArgs(1).RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task OneOfThreeSubjectConditions_SufficesForAFourConditionRecipe_BehindTheGate()
    {
        const string message = "Externe Personen, bitte.";
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(ExternsRecipe);
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    [TestCase(BulkEmployeesToGroupRecipe, "add everyone with a monthly contract in that region to the team, preview first", English)]
    [TestCase(BulkCustomersToNearestRecipe, "Kunden nach Ort zuordnen", German)]
    [TestCase(BulkEmployeesToNearestRecipe, "Mitarbeiter nach Ort zuordnen", German)]
    [TestCase(ExternsRecipe, "Externe nach Ort zuordnen", German)]
    [TestCase(SelectedClientsToGroupRecipe, "take the people I ticked in the list and add them to that team", English)]
    public async Task SemanticCandidate_NamingOnlyOneSubjectCondition_StaysACandidate(
        string recipeName, string message, string language)
    {
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(_seededRecipes.Where(r => r.Name == recipeName).ToList());
        StubRetrieval(message, (recipeName, StrongScore));

        var plan = await _service.ResolveAsync(message, language);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(recipeName);
        plan.NeedsConfirmation.ShouldBeTrue();
        await _retrieval.ReceivedWithAnyArgs(1).RetrieveAsync(
            default!, default!, default, default, default, default);
    }

    [Test]
    public async Task TwoOfThreeSubjectConditions_ResolveTheFourConditionRecipeBehindTheGate()
    {
        const string message = "Alle externen Mitarbeiter, bitte.";
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(ExternsRecipe);
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task LocaleBoundBulkMarkerAlone_WithoutTheGermanLanguage_DoesNotCountAsAnAnchor()
    {
        const string message = "Alle, bitte.";
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var plan = await _service.ResolveAsync(message);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task LocaleBoundBulkMarkerAlone_WithTheGermanLanguage_CountsAsAnAnchor()
    {
        const string message = "Alle, bitte.";
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(ExternsRecipe);
    }

    [Test]
    public async Task VerblessActionRequest_AnchoredBySubject_ResolvesTheTwoConditionRecipe()
    {
        const string message = "Neuen Mitarbeiter, bitte.";
        StubRetrieval(message, (OnboardRecipe, GreyZoneScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardRecipe);
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task PackLanguage_IsNotAnchorGated_KnownGap()
    {
        const string message = "Lee bitte mis notas aplazadas.";
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var plan = await _service.ResolveAsync(message, Spanish);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(ExternsRecipe);
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task AnchorlessTopCandidate_FallsThroughToTheNextAnchoredCandidate()
    {
        const string message = "Neuen Mitarbeiter, bitte.";
        StubRetrieval(message, (ExternsRecipe, StrongScore), (OnboardRecipe, GreyZoneScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardRecipe);
        plan.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task AnchorlessRunnerUp_IsNotSurfacedAsAlternative()
    {
        const string message = "Neuen Mitarbeiter, bitte.";
        StubRetrieval(message, (OnboardRecipe, TopScore), (ExternsRecipe, RunnerUpScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardRecipe);
        plan.AlternativeGoal.ShouldBeNull();
    }

    [Test]
    public async Task AnchoredRunnerUp_IsStillSurfacedAsAlternative()
    {
        const string message = "Neuen Mitarbeiter für das Team, bitte.";
        StubRetrieval(message, (OnboardRecipe, TopScore), (AddToGroupRecipe, RunnerUpScore));

        var plan = await _service.ResolveAsync(message, German);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(OnboardRecipe);
        plan.AlternativeGoal.ShouldBe(SeededGoal(AddToGroupRecipe));
    }

    [Test]
    public async Task GuaranteedSkillNamesAsync_DeferredNotesMessage_GuaranteesNothing()
    {
        const string message = "Lies bitte meine zurückgestellten Notizen vor.";
        StubRetrieval(message, (ExternsRecipe, StrongScore));

        var skills = await _service.GuaranteedSkillNamesAsync(
            userId: null, conversationId: null, message, language: German);

        skills.ShouldBeEmpty();
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}");
    }
}
