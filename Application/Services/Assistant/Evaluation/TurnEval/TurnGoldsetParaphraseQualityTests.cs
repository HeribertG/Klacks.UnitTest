// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Quality gate of the paraphrase goldset. Paraphrases are train data only: every id names a train item of the
/// default goldset and is itself train, every paraphrase inherits its source's expectation, none repeats a
/// message of the default goldset - a holdout message would leak into training, and any repeated message would
/// collide with the golden-case seeder, which is idempotent per (query, locale) and ignores the expectation -
/// and no source item carries more than TurnEvalDefaults.MaxParaphrasesPerSourceItem paraphrases. The checks
/// themselves live in TurnGoldsetParaphraseQualityChecks, exercised directly against fixtures by
/// TurnGoldsetParaphraseQualityChecksTests so they run before this file's own subject is ever generated.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetParaphraseQualityTests
{
    private const string GoldsetFileExtension = ".json";
    private const string GeneratorHint = "scripts/generate-goldset-paraphrases.ps1";

    private static readonly string[] GoldsetsRelativePath = ["Klacks.Api", "Application", "Skills", "Goldsets"];

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private List<TurnGoldsetItem> _paraphrases = null!;
    private Dictionary<string, TurnGoldsetItem> _baseById = null!;

    [OneTimeSetUp]
    public void LoadFiles()
    {
        var directory = LocateRepoDirectory(GoldsetsRelativePath);
        var paraphrasePath = Path.Combine(directory, TurnEvalDefaults.ParaphraseGoldset + GoldsetFileExtension);

        if (!File.Exists(paraphrasePath))
        {
            Assert.Ignore($"{TurnEvalDefaults.ParaphraseGoldset}{GoldsetFileExtension} is not generated yet; run {GeneratorHint}.");
        }

        _paraphrases = Load(paraphrasePath).Items;
        _baseById = Load(Path.Combine(directory, TurnEvalDefaults.DefaultGoldset + GoldsetFileExtension)).Items
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    [Test]
    public void TheFile_IsNotEmpty()
    {
        _paraphrases.ShouldNotBeEmpty();
    }

    [Test]
    public void EveryId_IsUniqueAndNamesATrainItemOfTheDefaultGoldset()
    {
        TurnGoldsetParaphraseQualityChecks.ValidateIdsAndPartitions(_paraphrases, _baseById).ShouldBeEmpty();
    }

    [Test]
    public void EverySource_HasAtMostMaxParaphrasesAndSuffixesInRange()
    {
        TurnGoldsetParaphraseQualityChecks.ValidateGroupSizeAndSuffixRange(_paraphrases).ShouldBeEmpty();
    }

    [Test]
    public void EveryParaphrase_InheritsTheExpectationOfItsSource()
    {
        TurnGoldsetParaphraseQualityChecks.ValidateExpectationInheritance(_paraphrases, _baseById).ShouldBeEmpty();
    }

    [Test]
    public void NoParaphrase_RepeatsAMessageOfTheDefaultGoldset()
    {
        TurnGoldsetParaphraseQualityChecks.FindMessagesRepeatingTheDefaultGoldset(_paraphrases, _baseById).ShouldBeEmpty();
    }

    [Test]
    public void NoParaphrase_RepeatsAHoldoutMessage()
    {
        TurnGoldsetParaphraseQualityChecks.FindMessagesRepeatingAHoldoutItem(_paraphrases, _baseById).ShouldBeEmpty();
    }

    [Test]
    public void NoTwoParaphrases_ShareAMessage()
    {
        TurnGoldsetParaphraseQualityChecks.FindDuplicateMessagesAmongParaphrases(_paraphrases).ShouldBeEmpty();
    }

    private static TurnGoldsetDocument Load(string path) =>
        JsonSerializer.Deserialize<TurnGoldsetDocument>(File.ReadAllText(path), SerializerOptions)
            .ShouldNotBeNull(path);

    private static string LocateRepoDirectory(string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} by walking up from the test base directory.");
    }
}
