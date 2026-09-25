// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the goldset import that finally gives the regression gate something to replay. Five rules
/// decide whether the gate can be trusted: only items that name an expected tool become cases, the
/// partition is the deterministic one every consumer recomputes, a second startup inserts nothing, an
/// over-long message is stored truncated - so the idempotency key has to be the truncated text too, or
/// every restart would insert the same two cases again - and a corrected expectation in the shipped file
/// reaches the database, because an insert-only seeder would leave the gate measuring the old answer
/// forever.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetGoldenCaseSeedLoaderTests
{
    private const string GoldsetName = TurnEvalDefaults.DefaultGoldset;
    private const string ApiProjectDirectory = "Klacks.Api";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ITurnGoldsetLoader _goldsetLoader = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private TurnGoldsetGoldenCaseSeedLoader _seeder = null!;
    private List<SkillLearningGoldenCase> _inserted = null!;
    private List<SkillLearningGoldenCase> _updated = null!;

    [SetUp]
    public void SetUp()
    {
        _inserted = [];
        _updated = [];

        _goldsetLoader = Substitute.For<ITurnGoldsetLoader>();
        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ListByOriginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _goldenCases
            .When(x => x.AddRangeAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>()))
            .Do(call => _inserted.AddRange(call.Arg<IReadOnlyList<SkillLearningGoldenCase>>()));
        _goldenCases
            .When(x => x.UpdateRangeAsync(
                Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>()))
            .Do(call => _updated.AddRange(call.Arg<IReadOnlyList<SkillLearningGoldenCase>>()));

        _seeder = new TurnGoldsetGoldenCaseSeedLoader(
            _goldsetLoader, _goldenCases, NullLogger<TurnGoldsetGoldenCaseSeedLoader>.Instance);
    }

    private void GivenItems(params TurnGoldsetItem[] items) =>
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);

    [Test]
    public async Task OnlyItemsThatNameAnExpectedTool_BecomeGoldenCases()
    {
        GivenItems(
            new TurnGoldsetItem { Id = "ts-001", Message = "Hallo", Locale = "de" },
            new TurnGoldsetItem
            {
                Id = "ts-011", Message = "Kunde anlegen", Locale = "de", ExpectedTool = "create_client"
            });

        await _seeder.LoadAsync();

        _inserted.Count.ShouldBe(1);
        _inserted[0].Query.ShouldBe("Kunde anlegen");
        _inserted[0].Locale.ShouldBe("de");
        _inserted[0].ExpectedSourceId.ShouldBe("create_client");
        _inserted[0].Origin.ShouldBe(GoldenCaseOrigins.Goldset);
    }

    [Test]
    public async Task ThePartition_IsTheOneEveryConsumerRecomputesFromTheItemId()
    {
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-011", Message = "Kunde anlegen", Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted[0].Partition.ShouldBe(GoldsetPartitioner.Resolve("ts-011"));
    }

    [Test]
    public async Task AnAlreadySeededQueryAndLocale_IsNotInsertedAgain()
    {
        _goldenCases.ListByOriginAsync(GoldenCaseOrigins.Goldset, Arg.Any<CancellationToken>())
            .Returns([
                new SkillLearningGoldenCase
                {
                    Query = "Kunde anlegen",
                    Locale = "de",
                    ExpectedSourceId = "create_client",
                    Origin = GoldenCaseOrigins.Goldset
                }
            ]);
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-011", Message = "Kunde anlegen", Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted.ShouldBeEmpty();
    }

    [Test]
    public async Task AMessageLongerThanTheExcerptLimit_IsStoredTruncated()
    {
        var longMessage = new string('a', SkillLearningDefaults.ExcerptMaxLength + 40);
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-long", Message = longMessage, Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted[0].Query.Length.ShouldBe(SkillLearningDefaults.ExcerptMaxLength);
        _inserted[0].Query.ShouldBe(longMessage[..SkillLearningDefaults.ExcerptMaxLength]);
    }

    // The stored query is the truncated one, so the key has to be too - otherwise the next startup
    // would compare the full message against a truncated row and insert a duplicate every time.
    [Test]
    public async Task TheIdempotencyKeyOfATruncatedCase_IsTheTruncatedQuery()
    {
        var longMessage = new string('a', SkillLearningDefaults.ExcerptMaxLength + 40);
        _goldenCases.ListByOriginAsync(GoldenCaseOrigins.Goldset, Arg.Any<CancellationToken>())
            .Returns([
                new SkillLearningGoldenCase
                {
                    Query = longMessage[..SkillLearningDefaults.ExcerptMaxLength],
                    Locale = "de",
                    ExpectedSourceId = "create_client",
                    Origin = GoldenCaseOrigins.Goldset
                }
            ]);
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-long", Message = longMessage, Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted.ShouldBeEmpty();
    }

    // An insert-only seeder keyed on the query means a corrected expectation in the shipped file never
    // reaches the database: the gate would keep measuring against the answer that was wrong.
    [Test]
    public async Task ACorrectedExpectedTool_UpdatesTheExistingRowInsteadOfInsertingOne()
    {
        var stored = Existing("Kunde anlegen", "de", "list_clients");
        _goldenCases.ListByOriginAsync(GoldenCaseOrigins.Goldset, Arg.Any<CancellationToken>())
            .Returns([stored]);
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-011", Message = "Kunde anlegen", Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted.ShouldBeEmpty();
        _updated.ShouldHaveSingleItem().ShouldBeSameAs(stored);
        stored.ExpectedSourceId.ShouldBe("create_client");
    }

    [Test]
    public async Task AnUnchangedExpectedTool_IsNeitherInsertedNorUpdated()
    {
        _goldenCases.ListByOriginAsync(GoldenCaseOrigins.Goldset, Arg.Any<CancellationToken>())
            .Returns([Existing("Kunde anlegen", "de", "create_client")]);
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-011", Message = "Kunde anlegen", Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted.ShouldBeEmpty();
        _updated.ShouldBeEmpty();
    }

    // An edited message is a different key, so it becomes a new case and the old row stays - the seeder
    // prunes nothing, because it cannot tell an edit from a case the loop itself froze.
    [Test]
    public async Task AnEditedMessage_BecomesANewCaseAndLeavesTheOldRowAlone()
    {
        var stored = Existing("Kunde anlegen", "de", "create_client");
        _goldenCases.ListByOriginAsync(GoldenCaseOrigins.Goldset, Arg.Any<CancellationToken>())
            .Returns([stored]);
        GivenItems(new TurnGoldsetItem
        {
            Id = "ts-011", Message = "Neuen Kunden anlegen", Locale = "de", ExpectedTool = "create_client"
        });

        await _seeder.LoadAsync();

        _inserted.ShouldHaveSingleItem().Query.ShouldBe("Neuen Kunden anlegen");
        _updated.ShouldBeEmpty();
    }

    [Test]
    public async Task AMissingGoldsetFile_InsertsNothingAndDoesNotThrow()
    {
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TurnGoldsetItem>>(_ => throw new FileNotFoundException("no goldset"));

        await _seeder.LoadAsync();

        await _goldenCases.DidNotReceive().AddRangeAsync(
            Arg.Any<IReadOnlyList<SkillLearningGoldenCase>>(), Arg.Any<CancellationToken>());
    }

    // The numbers the acceptance criteria name, measured against the file that actually ships.
    [Test]
    public void TheShippedGoldset_Yields304CasesSplit220TrainAnd84Holdout()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), ApiProjectDirectory,
            "Application", "Skills", "Goldsets", GoldsetName + ".json");

        var document = JsonSerializer.Deserialize<TurnGoldsetDocument>(
            File.ReadAllText(path), SerializerOptions);

        document.ShouldNotBeNull();
        var withTool = document!.Items.Where(i => !string.IsNullOrWhiteSpace(i.ExpectedTool)).ToList();

        withTool.Count.ShouldBe(304);
        withTool.Count(i => GoldsetPartitioner.Resolve(i.Id) == GoldenCasePartitions.Train).ShouldBe(220);
        withTool.Count(i => GoldsetPartitioner.Resolve(i.Id) == GoldenCasePartitions.Holdout).ShouldBe(84);
    }

    private static SkillLearningGoldenCase Existing(string query, string locale, string expectedSourceId) =>
        new()
        {
            Id = Guid.NewGuid(),
            Query = query,
            Locale = locale,
            ExpectedSourceId = expectedSourceId,
            Origin = GoldenCaseOrigins.Goldset,
            Partition = GoldenCasePartitions.Holdout
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root with Klacks.Api not found.");
    }
}
