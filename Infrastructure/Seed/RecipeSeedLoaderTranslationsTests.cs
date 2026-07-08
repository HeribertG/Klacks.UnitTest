// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that the recipe seed loader carries the localized fallback texts end to end: goalTranslations
/// land on the AgentRecipe entity (insert and version-bump update) and promptTranslations survive the
/// steps round-trip into StepsJson in camelCase, so the runtime engine (which deserializes StepsJson
/// case-insensitively) can hand them to the deterministic reply fallbacks.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class RecipeSeedLoaderTranslationsTests
{
    private const string RecipeName = "add-employee-to-group";

    private string _contentRoot = null!;
    private IAgentRecipeRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-recipe-seed-i18n-" + Guid.NewGuid().ToString("N"));
        var definitionsDir = Path.Combine(_contentRoot, "Application", "Skills", "Definitions");
        Directory.CreateDirectory(definitionsDir);
        File.WriteAllText(Path.Combine(definitionsDir, "recipe-seeds.json"), SeedJson(version: 2));

        _repository = Substitute.For<IAgentRecipeRepository>();
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
    public async Task Insert_CarriesGoalTranslations_AndPromptTranslationsIntoStepsJson()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        AgentRecipe? inserted = null;
        await _repository.AddAsync(Arg.Do<AgentRecipe>(r => inserted = r), Arg.Any<CancellationToken>());

        await CreateLoader().LoadAsync();

        await _repository.Received(1).AddAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
        Assert.That(inserted, Is.Not.Null);
        Assert.That(inserted!.GoalTranslations, Is.Not.Null);
        Assert.That(inserted.GoalTranslations!["de"], Is.EqualTo("Mitarbeiter zur Gruppe hinzufügen."));

        var steps = JsonSerializer.Deserialize<List<RecipeStep>>(
            inserted.StepsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.That(steps, Is.Not.Null);
        Assert.That(steps![0].PromptTranslations, Is.Not.Null);
        Assert.That(steps[0].PromptTranslations!["fr"], Is.EqualTo("Quel nom ?"));
        Assert.That(inserted.StepsJson, Does.Contain("promptTranslations"),
            "steps must serialize the translations in camelCase for the runtime deserializer");
    }

    [Test]
    public async Task VersionBumpReseed_UpdatesGoalTranslations()
    {
        var existing = new AgentRecipe
        {
            Name = RecipeName,
            Goal = "old goal",
            Version = 1,
            GoalTranslations = new Dictionary<string, string> { ["de"] = "Alter Zieltext." }
        };
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { existing });

        AgentRecipe? updated = null;
        await _repository.UpdateAsync(Arg.Do<AgentRecipe>(r => updated = r), Arg.Any<CancellationToken>());

        await CreateLoader().LoadAsync();

        await _repository.Received(1).UpdateAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.GoalTranslations, Is.Not.Null);
        Assert.That(updated.GoalTranslations!["de"], Is.EqualTo("Mitarbeiter zur Gruppe hinzufügen."));
    }

    private RecipeSeedLoader CreateLoader()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);
        return new RecipeSeedLoader(_repository, environment, NullLogger<RecipeSeedLoader>.Instance);
    }

    private static string SeedJson(int version) =>
        "{\"version\":1,\"recipes\":[{" +
        $"\"name\":\"{RecipeName}\"," +
        "\"goal\":\"new goal\"," +
        "\"goalTranslations\":{\"de\":\"Mitarbeiter zur Gruppe hinzufügen.\",\"en\":\"Add employee to group.\"}," +
        $"\"version\":{version}," +
        "\"isEnabled\":true," +
        "\"sortOrder\":10," +
        "\"trigger\":{\"allOf\":[{\"anySubstring\":[\"gruppe\"]}]}," +
        "\"steps\":[{\"kind\":\"ask\",\"slot\":\"name\",\"prompt\":\"Ask the name.\"," +
        "\"promptTranslations\":{\"de\":\"Wie ist der Name?\",\"fr\":\"Quel nom ?\"}}]" +
        "}]}";
}
