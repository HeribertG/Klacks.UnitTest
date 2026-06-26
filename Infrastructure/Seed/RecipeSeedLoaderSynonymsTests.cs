// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves the reason recipe synonyms live on the entity (variant B) rather than merged into the trigger
/// JSON (variant A): a seed version bump updates goal/trigger/steps/sortOrder/version but must leave the
/// per-language Synonyms — installed by a language plugin — untouched, so multi-language coverage survives
/// every recipe reseed without re-installing the plugin.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class RecipeSeedLoaderSynonymsTests
{
    private const string RecipeName = "add-employee-to-group";

    private string _contentRoot = null!;
    private IAgentRecipeRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-recipe-seed-" + Guid.NewGuid().ToString("N"));
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
    public async Task VersionBumpReseed_PreservesInstalledSynonyms()
    {
        var existing = new AgentRecipe
        {
            Name = RecipeName,
            Goal = "old goal",
            Version = 1,
            Synonyms = new Dictionary<string, List<string>>
            {
                ["es"] = ["incorporar un empleado al grupo"]
            }
        };
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { existing });

        AgentRecipe? updated = null;
        await _repository.UpdateAsync(Arg.Do<AgentRecipe>(r => updated = r), Arg.Any<CancellationToken>());

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);
        var loader = new RecipeSeedLoader(_repository, environment, NullLogger<RecipeSeedLoader>.Instance);

        await loader.LoadAsync();

        await _repository.Received(1).UpdateAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.Version, Is.EqualTo(2), "the version bump must be applied");
        Assert.That(updated.Goal, Is.EqualTo("new goal"), "goal must be updated from the seed");
        Assert.That(updated.Synonyms, Is.Not.Null, "installed synonyms must survive the reseed");
        Assert.That(updated.Synonyms!["es"], Does.Contain("incorporar un empleado al grupo"));
    }

    private static string SeedJson(int version) =>
        "{\"version\":1,\"recipes\":[{" +
        $"\"name\":\"{RecipeName}\"," +
        "\"goal\":\"new goal\"," +
        $"\"version\":{version}," +
        "\"isEnabled\":true," +
        "\"sortOrder\":10," +
        "\"trigger\":{\"allOf\":[{\"anySubstring\":[\"gruppe\"]}]}," +
        "\"steps\":[{\"kind\":\"ask\",\"slot\":\"name\",\"prompt\":\"Ask the name.\"}]" +
        "}]}";
}
