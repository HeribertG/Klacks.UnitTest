// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The optimizer reads the selection misses of each learning goldset with one limited query per goldset. The
/// limit is a safety ceiling, not a sample: if a goldset had more train items than the limit, the misses past it
/// would be cut off by the limit and could only be reached once a later run replaced the ones in front. The
/// paraphrase goldset is generated later (three paraphrases per train item of the default goldset), so its file
/// may be missing; its projected size is checked from the default goldset instead.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class LearningGoldsetMissLimitGuardTests
{
    private const int ParaphrasesPerTrainItem = 3;
    private const string JsonExtension = ".json";
    private const string ItemsProperty = "items";
    private const string IdProperty = "id";

    private static readonly string[] RepoGoldsetRelativePath = ["Klacks.Api", "Application", "Skills", "Goldsets"];

    [Test]
    public void TheMissLimit_ExceedsTheTrainItemsOfEveryExistingLearningGoldset()
    {
        var checkedGoldsets = 0;
        foreach (var goldset in TurnEvalDefaults.LearningGoldsets)
        {
            var path = Path.Combine(GoldsetDirectory(), goldset + JsonExtension);
            if (!File.Exists(path))
            {
                continue;
            }

            checkedGoldsets++;
            SkillLearningDefaults.MaxGoldsetMissesPerRun.ShouldBeGreaterThan(
                TrainItemCount(path), $"train items of goldset '{goldset}'");
        }

        checkedGoldsets.ShouldBeGreaterThan(0);
    }

    [Test]
    public void TheMissLimit_ExceedsTheProjectedSizeOfTheParaphraseGoldset()
    {
        var defaultTrainItems = TrainItemCount(
            Path.Combine(GoldsetDirectory(), TurnEvalDefaults.DefaultGoldset + JsonExtension));

        SkillLearningDefaults.MaxGoldsetMissesPerRun.ShouldBeGreaterThan(defaultTrainItems * ParaphrasesPerTrainItem);
    }

    private static int TrainItemCount(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(ItemsProperty).EnumerateArray()
            .Select(item => item.TryGetProperty(IdProperty, out var id) ? id.GetString() : null)
            .Count(id => !string.IsNullOrWhiteSpace(id) && GoldsetPartitioner.IsTrain(id));
    }

    private static string GoldsetDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. RepoGoldsetRelativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', RepoGoldsetRelativePath)} by walking up from the test base directory.");
    }
}
