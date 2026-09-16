// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that correction-v1.json never drifts from the authored skill catalog. TurnReplayService builds
/// its SkillLabels dictionary by wrapping PreviousTurn.SkillDisplayLabel under the item's own Locale
/// (TurnReplayService.ReplayLabels) - it never resolves the label from skill-seeds.json. That makes a
/// goldset item's hand-typed label the ONLY thing standing between a replay and a fabricated pass: if the
/// fixture text no longer matches what the catalog actually authors for that skill and locale, the replay
/// exercises the graceful-correction "name the previous action" rule against a sentence production would
/// never produce. This guard makes that drift a red test instead of a silent one.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class CorrectionGoldsetLabelGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SkillSeedRelativePath = "Application/Skills/Definitions/skill-seeds.json";
    private const string GoldsetRelativePath = "Application/Skills/Goldsets/correction-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static Dictionary<string, Dictionary<string, string>> _catalog = null!;
    private static List<TurnGoldsetItem> _items = null!;

    private sealed class SeedFile
    {
        public List<SeedSkill> Skills { get; set; } = [];
    }

    private sealed class SeedSkill
    {
        public string Name { get; set; } = string.Empty;

        public Dictionary<string, string>? Labels { get; set; }
    }

    [OneTimeSetUp]
    public void LoadOnce()
    {
        var apiRoot = LocateApiRoot();

        _catalog = Deserialize<SeedFile>(Path.Combine(apiRoot, SkillSeedRelativePath)).Skills
            .Where(skill => skill.Labels != null)
            .ToDictionary(skill => skill.Name, skill => skill.Labels!);

        _items = Deserialize<TurnGoldsetDocument>(Path.Combine(apiRoot, GoldsetRelativePath)).Items.ToList();
    }

    // Only the four core languages are checked: pack-language labels (es, pl, zh-CN, ...) are not yet
    // authored in the catalog at all (Block 3 is still open), so this goldset's hand-typed pack labels
    // are a deliberate, owner-accepted stand-in until the pack label files exist - not drift.
    [Test]
    public void EveryCoreLanguageItem_NamesThePreviousActionAsTheCatalogDoes()
    {
        var problems = new List<string>();

        foreach (var item in _items)
        {
            var previousTurn = item.PreviousTurn;
            if (previousTurn == null
                || string.IsNullOrWhiteSpace(previousTurn.SkillDisplayLabel)
                || item.Locale == null
                || !LanguagePluginConstants.CoreLanguages.Contains(item.Locale))
            {
                continue;
            }

            if (!_catalog.TryGetValue(previousTurn.CalledSkill, out var labels)
                || !labels.TryGetValue(item.Locale, out var catalogLabel))
            {
                problems.Add($"{item.Id}: no catalog label for {previousTurn.CalledSkill}/{item.Locale}");
                continue;
            }

            if (catalogLabel != previousTurn.SkillDisplayLabel)
            {
                problems.Add(
                    $"{item.Id}: previousTurn.skillDisplayLabel '{previousTurn.SkillDisplayLabel}' does not "
                    + $"match the catalog label '{catalogLabel}' for {previousTurn.CalledSkill}/{item.Locale}");
            }
        }

        problems.ShouldBeEmpty(
            $"{problems.Count} goldset item(s) name the corrected skill differently than the catalog does - "
            + $"the replay would test a sentence production never produces:{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems));
    }

    private static T Deserialize<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} deserialized to null.");

    private static string LocateApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory, SkillSeedRelativePath);
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, ApiProjectDirectory);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{SkillSeedRelativePath} by walking up from the test base directory.");
    }
}
