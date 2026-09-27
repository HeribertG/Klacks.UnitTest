// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pure paraphrase-goldset quality checks, each returning the violations it finds as plain text. Shared by
/// TurnGoldsetParaphraseQualityTests, which runs them against the generated file (Ignored while it does not
/// exist), and TurnGoldsetParaphraseQualityChecksTests, which runs the same checks against small in-memory
/// fixtures so the checking logic itself is exercised before the file is ever generated.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Globalization;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

public static class TurnGoldsetParaphraseQualityChecks
{
    private const string SourceGroup = "source";
    private const string SuffixGroup = "n";

    public static readonly Regex ParaphraseIdRegex = new(
        "^" + Regex.Escape(TurnEvalDefaults.ParaphraseItemIdPrefix) + "(?<source>.+)-(?<n>[0-9]+)$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static List<string> ValidateIdsAndPartitions(
        IReadOnlyList<TurnGoldsetItem> paraphrases, IReadOnlyDictionary<string, TurnGoldsetItem> baseById)
    {
        var violations = new List<string>();

        violations.AddRange(paraphrases
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate id '{group.Key}'"));

        foreach (var item in paraphrases)
        {
            var match = ParaphraseIdRegex.Match(item.Id);
            if (!match.Success)
            {
                violations.Add($"'{item.Id}' is not para-<sourceId>-<n>");
                continue;
            }

            var source = match.Groups[SourceGroup].Value;
            if (!baseById.ContainsKey(source))
            {
                violations.Add($"'{item.Id}': source '{source}' is not an item of {TurnEvalDefaults.DefaultGoldset}");
            }
            else if (!GoldsetPartitioner.IsTrain(source))
            {
                violations.Add($"'{item.Id}': source '{source}' is a holdout item");
            }

            if (!GoldsetPartitioner.IsTrain(item.Id))
            {
                violations.Add($"'{item.Id}' is not in the train partition");
            }
        }

        return violations;
    }

    public static List<string> ValidateGroupSizeAndSuffixRange(IReadOnlyList<TurnGoldsetItem> paraphrases)
    {
        var violations = new List<string>();
        var bySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var item in paraphrases)
        {
            var match = ParaphraseIdRegex.Match(item.Id);
            if (!match.Success)
            {
                continue;
            }

            var source = match.Groups[SourceGroup].Value;
            if (!bySource.TryGetValue(source, out var ids))
            {
                ids = new List<string>();
                bySource[source] = ids;
            }

            ids.Add(item.Id);

            var suffixText = match.Groups[SuffixGroup].Value;
            if (!int.TryParse(suffixText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)
                || suffix < 1
                || suffix > TurnEvalDefaults.MaxParaphrasesPerSourceItem)
            {
                violations.Add(
                    $"'{item.Id}': suffix '{suffixText}' is outside 1..{TurnEvalDefaults.MaxParaphrasesPerSourceItem}");
            }
        }

        violations.AddRange(bySource
            .Where(entry => entry.Value.Count > TurnEvalDefaults.MaxParaphrasesPerSourceItem)
            .Select(entry =>
                $"source '{entry.Key}' has {entry.Value.Count} paraphrases, more than {TurnEvalDefaults.MaxParaphrasesPerSourceItem}"));

        return violations;
    }

    public static List<string> ValidateExpectationInheritance(
        IReadOnlyList<TurnGoldsetItem> paraphrases, IReadOnlyDictionary<string, TurnGoldsetItem> baseById)
    {
        var violations = new List<string>();

        foreach (var item in paraphrases)
        {
            var match = ParaphraseIdRegex.Match(item.Id);
            if (!match.Success || !baseById.TryGetValue(match.Groups[SourceGroup].Value, out var source))
            {
                continue;
            }

            if (!string.Equals(item.ExpectedTool, source.ExpectedTool, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': expectedTool '{item.ExpectedTool}' differs from '{source.ExpectedTool}'");
            }

            if (!string.Equals(item.Locale, source.Locale, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': locale '{item.Locale}' differs from '{source.Locale}'");
            }

            if (!item.AlternativeTools.SequenceEqual(source.AlternativeTools, StringComparer.Ordinal))
            {
                violations.Add($"'{item.Id}': alternativeTools differ from the source");
            }

            if (item.ExpectedTool == null || item.ExpectedSlots.Count > 0 || item.ExpectedRecipe != null)
            {
                violations.Add($"'{item.Id}': a paraphrase expects a tool and neither slots nor a recipe");
            }
        }

        return violations;
    }

    public static List<string> FindMessagesRepeatingTheDefaultGoldset(
        IReadOnlyList<TurnGoldsetItem> paraphrases, IReadOnlyDictionary<string, TurnGoldsetItem> baseById)
    {
        var baseMessages = baseById.Values.Select(item => Normalize(item.Message)).ToHashSet(StringComparer.Ordinal);

        return paraphrases
            .Where(item => baseMessages.Contains(Normalize(item.Message)))
            .Select(item => item.Id)
            .ToList();
    }

    public static List<string> FindMessagesRepeatingAHoldoutItem(
        IReadOnlyList<TurnGoldsetItem> paraphrases, IReadOnlyDictionary<string, TurnGoldsetItem> baseById)
    {
        var holdoutMessages = baseById.Values
            .Where(item => GoldsetPartitioner.IsHoldout(item.Id))
            .Select(item => Normalize(item.Message))
            .ToHashSet(StringComparer.Ordinal);

        return paraphrases
            .Where(item => holdoutMessages.Contains(Normalize(item.Message)))
            .Select(item => item.Id)
            .ToList();
    }

    public static List<string> FindDuplicateMessagesAmongParaphrases(IReadOnlyList<TurnGoldsetItem> paraphrases)
    {
        return paraphrases
            .GroupBy(item => (Normalize(item.Message), item.Locale ?? string.Empty))
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(item => item.Id)))
            .ToList();
    }

    public static string Normalize(string message) => message.Trim().ToLowerInvariant();
}
