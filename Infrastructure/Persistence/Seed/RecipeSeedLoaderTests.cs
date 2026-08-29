// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the one property of the seed loader that stage G3 depends on: it owns Seed rows and nothing
/// else. A learned capability lives in the same table as the hand-written recipes, and a deployment
/// silently reverting something Klacksy composed would leave no trace anywhere - the recipe would simply
/// stop behaving the way the card says it does.
/// The loader is driven against a real temporary seed file rather than a mocked reader, because the
/// behaviour under test is a branch inside its file-processing loop.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class RecipeSeedLoaderTests
{
    private const string RecipeName = "create-shift-order";
    private const string SeedRelativePath = "Application/Skills/Definitions/recipe-seeds.json";

    private string _contentRoot = null!;
    private IAgentRecipeRepository _recipes = null!;
    private ISkillPhraseRepository _phrases = null!;
    private RecipeSeedLoader _loader = null!;

    [SetUp]
    public void SetUp()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-recipe-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "Application", "Skills", "Definitions"));

        _recipes = Substitute.For<IAgentRecipeRepository>();
        _phrases = Substitute.For<ISkillPhraseRepository>();

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);

        _loader = new RecipeSeedLoader(
            _recipes, _phrases, environment, Substitute.For<ILogger<RecipeSeedLoader>>());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task ALearnedRecipe_IsNeverOverwrittenEvenByAHigherSeedVersion()
    {
        var learned = ExistingRecipe(AgentRecipeOrigins.Learned, version: 1, goal: "What Klacksy composed");
        GivenExisting(learned);
        GivenSeedFile(version: 9, goal: "What the seed file says");

        await _loader.LoadAsync();

        learned.Goal.ShouldBe("What Klacksy composed");
        learned.Version.ShouldBe(1);
        await _recipes.DidNotReceive().UpdateAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASeedRecipe_IsStillUpdatedByAHigherSeedVersion()
    {
        var seeded = ExistingRecipe(AgentRecipeOrigins.Seed, version: 1, goal: "Old goal");
        GivenExisting(seeded);
        GivenSeedFile(version: 2, goal: "New goal");

        await _loader.LoadAsync();

        seeded.Goal.ShouldBe("New goal");
        seeded.Version.ShouldBe(2);
        await _recipes.Received(1).UpdateAsync(seeded, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASeedRecipeAtTheSameVersion_IsLeftAlone()
    {
        var seeded = ExistingRecipe(AgentRecipeOrigins.Seed, version: 3, goal: "Unchanged");
        GivenExisting(seeded);
        GivenSeedFile(version: 3, goal: "Something else");

        await _loader.LoadAsync();

        seeded.Goal.ShouldBe("Unchanged");
        await _recipes.DidNotReceive().UpdateAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ANewSeedRecipe_IsInsertedWithSeedOrigin()
    {
        GivenExisting();
        GivenSeedFile(version: 1, goal: "Brand new");

        await _loader.LoadAsync();

        await _recipes.Received(1).AddAsync(
            Arg.Is<AgentRecipe>(r => r.Origin == AgentRecipeOrigins.Seed), Arg.Any<CancellationToken>());
    }

    private void GivenExisting(params AgentRecipe[] recipes) =>
        _recipes.GetAllAsync(Arg.Any<CancellationToken>()).Returns([.. recipes]);

    private static AgentRecipe ExistingRecipe(string origin, int version, string goal) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = RecipeName,
            Goal = goal,
            Version = version,
            Origin = origin
        };

    private void GivenSeedFile(int version, string goal)
    {
        var payload = new
        {
            version = 1,
            recipes = new[]
            {
                new
                {
                    name = RecipeName,
                    goal,
                    goalTranslations = new Dictionary<string, string> { ["de"] = goal },
                    trigger = new { allOf = new[] { new { anyWordStart = new[] { "dienstauftrag" } } } },
                    steps = new[] { new { kind = "search", skill = "list_open_shifts" } },
                    isEnabled = true,
                    sortOrder = 10,
                    version
                }
            }
        };

        File.WriteAllText(
            Path.Combine(_contentRoot, SeedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(payload));
    }
}
