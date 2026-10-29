// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the one property of the seed loader that stage G3 depends on: it owns Seed rows and nothing
/// else. A learned capability lives in the same table as the hand-written recipes, and a deployment
/// silently reverting something Klacksy composed would leave no trace anywhere - the recipe would simply
/// stop behaving the way the card says it does. A Seed row the seed file no longer carries is disabled,
/// never deleted, and a higher seed version enables it again.
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
    private const string OrphanName = "seal-shift-order";
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

    [Test]
    public async Task ASeedRecipeNoLongerInTheSeedFile_IsDisabledNotDeleted()
    {
        var orphan = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Removed from seed", name: OrphanName);
        GivenExisting(orphan);
        GivenSeedFile(version: 1, goal: "Still seeded");

        await _loader.LoadAsync();

        orphan.IsEnabled.ShouldBeFalse();
        await _recipes.Received(1).UpdateAsync(orphan, Arg.Any<CancellationToken>());
        await _recipes.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ALearnedRecipeNotInTheSeedFile_StaysEnabled()
    {
        var learned = ExistingRecipe(AgentRecipeOrigins.Learned, version: 1, goal: "Composed", name: OrphanName);
        GivenExisting(learned);
        GivenSeedFile(version: 1, goal: "Still seeded");

        await _loader.LoadAsync();

        learned.IsEnabled.ShouldBeTrue();
        await _recipes.DidNotReceive().UpdateAsync(learned, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAlreadyDisabledOrphan_IsNotWrittenAgain()
    {
        var orphan = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Removed", name: OrphanName);
        orphan.IsEnabled = false;
        GivenExisting(orphan);
        GivenSeedFile(version: 1, goal: "Still seeded");

        await _loader.LoadAsync();

        await _recipes.DidNotReceive().UpdateAsync(orphan, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASeedRecipeStillInTheSeedFile_StaysEnabled()
    {
        var seeded = ExistingRecipe(AgentRecipeOrigins.Seed, version: 3, goal: "Unchanged");
        GivenExisting(seeded);
        GivenSeedFile(version: 3, goal: "Unchanged");

        await _loader.LoadAsync();

        seeded.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task ARetiredRecipePutBackWithAHigherVersion_IsEnabledAgain()
    {
        var retired = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Retired");
        retired.IsEnabled = false;
        GivenExisting(retired);
        GivenSeedFile(version: 3, goal: "Back again");

        await _loader.LoadAsync();

        retired.IsEnabled.ShouldBeTrue();
        retired.Version.ShouldBe(3);
    }

    [Test]
    public async Task AnEmptySeedFile_RetiresNothing()
    {
        var seeded = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Seeded");
        GivenExisting(seeded);
        GivenSeedFileWithNames();

        await _loader.LoadAsync();

        seeded.IsEnabled.ShouldBeTrue();
        await _recipes.DidNotReceive().UpdateAsync(Arg.Any<AgentRecipe>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASeedFileWithOnlyNamelessEntries_RetiresNothing()
    {
        var seeded = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Seeded");
        GivenExisting(seeded);
        GivenSeedFileWithNames(" ");

        await _loader.LoadAsync();

        seeded.IsEnabled.ShouldBeTrue();
        await _recipes.DidNotReceive().UpdateAsync(seeded, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASeedNameInOtherCasing_KeepsTheRecipeEnabled()
    {
        var seeded = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Seeded");
        GivenExisting(seeded);
        GivenSeedFileWithNames(RecipeName.ToUpperInvariant());

        await _loader.LoadAsync();

        seeded.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task AnAdminRecipeNotInTheSeedFile_StaysEnabled()
    {
        var admin = ExistingRecipe(AgentRecipeOrigins.Admin, version: 1, goal: "Admin made", name: OrphanName);
        GivenExisting(admin);
        GivenSeedFile(version: 1, goal: "Still seeded");

        await _loader.LoadAsync();

        admin.IsEnabled.ShouldBeTrue();
        await _recipes.DidNotReceive().UpdateAsync(admin, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARetiredRecipeRestoredAtTheSameVersion_StaysDisabled()
    {
        var retired = ExistingRecipe(AgentRecipeOrigins.Seed, version: 2, goal: "Retired");
        retired.IsEnabled = false;
        GivenExisting(retired);
        GivenSeedFile(version: 2, goal: "Restored by revert");

        await _loader.LoadAsync();

        retired.IsEnabled.ShouldBeFalse();
    }

    private void GivenExisting(params AgentRecipe[] recipes) =>
        _recipes.GetAllAsync(Arg.Any<CancellationToken>()).Returns([.. recipes]);

    private static AgentRecipe ExistingRecipe(string origin, int version, string goal, string name = RecipeName) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Goal = goal,
            Version = version,
            Origin = origin,
            IsEnabled = true
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

        WriteSeedFile(payload);
    }

    private void GivenSeedFileWithNames(params string[] names)
    {
        var payload = new
        {
            version = 1,
            recipes = names.Select(name => new
            {
                name,
                goal = "Seeded",
                trigger = new { allOf = new[] { new { anyWordStart = new[] { "dienstauftrag" } } } },
                steps = new[] { new { kind = "search", skill = "list_open_shifts" } },
                isEnabled = true,
                sortOrder = 10,
                version = 1
            }).ToArray()
        };

        WriteSeedFile(payload);
    }

    private void WriteSeedFile(object payload) =>
        File.WriteAllText(
            Path.Combine(_contentRoot, SeedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(payload));
}
