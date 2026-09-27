// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pure quality checks of the translated goldset turn-selection-v1-i18n, each returning the violations it finds
/// as plain text. Shared by TurnGoldsetI18nQualityTests, which runs them against the generated file (Ignored
/// while it does not exist), and TurnGoldsetI18nQualityChecksTests, which runs them against small in-memory
/// fixtures so the checking logic is exercised before the file is ever generated.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

public static class TurnGoldsetI18nQualityChecks
{
    public const int MinItemsPerLocale = 40;
    public const string GermanLocale = "de";
    public const string TranslationSource = "translation";

    public static readonly IReadOnlyList<string> CoreLocales = [GermanLocale, "en", "fr", "it"];

    public static IReadOnlyList<string> NonGermanTargetLocales(IEnumerable<string> languagePackLocales) =>
    [
        .. CoreLocales.Concat(languagePackLocales)
            .Where(locale => !string.Equals(locale, GermanLocale, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    public static List<string> ValidateIdsSourcesAndPartitions(
        IReadOnlyList<TurnGoldsetItem> translations, IReadOnlyDictionary<string, TurnGoldsetItem> baseById)
    {
        var violations = new List<string>();

        violations.AddRange(translations
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"duplicate id '{group.Key}'"));

        foreach (var item in translations)
        {
            if (!GoldsetTranslationId.TryParse(item.Id, out var locale, out var sourceId))
            {
                violations.Add($"'{item.Id}' is not {TurnEvalDefaults.I18nItemIdPrefix}<locale>{GoldsetTranslationId.SourceSeparator}<sourceId>");
                continue;
            }

            if (!string.Equals(locale, item.Locale, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': id locale '{locale}' differs from the item locale '{item.Locale}'");
            }

            if (!baseById.TryGetValue(sourceId, out var source))
            {
                violations.Add($"'{item.Id}': source '{sourceId}' is not an item of {TurnEvalDefaults.DefaultGoldset}");
                continue;
            }

            if (source.ExpectedTool == null || source.ExpectedRecipe != null || source.PreviousTurn != null)
            {
                violations.Add($"'{item.Id}': source '{sourceId}' is not a single-turn item expecting a tool");
            }

            if (GoldsetPartitioner.Resolve(item.Id) != GoldsetPartitioner.Resolve(sourceId))
            {
                violations.Add($"'{item.Id}': partition differs from the partition of its source '{sourceId}'");
            }
        }

        return violations;
    }

    public static List<string> ValidateInheritance(
        IReadOnlyList<TurnGoldsetItem> translations, IReadOnlyDictionary<string, TurnGoldsetItem> baseById)
    {
        var violations = new List<string>();

        foreach (var item in translations)
        {
            if (!GoldsetTranslationId.TryParse(item.Id, out _, out var sourceId)
                || !baseById.TryGetValue(sourceId, out var source))
            {
                continue;
            }

            if (!string.Equals(item.ExpectedTool, source.ExpectedTool, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': expectedTool '{item.ExpectedTool}' differs from '{source.ExpectedTool}'");
            }

            if (!item.AlternativeTools.SequenceEqual(source.AlternativeTools, StringComparer.Ordinal))
            {
                violations.Add($"'{item.Id}': alternativeTools differ from the source");
            }

            if (!string.Equals(item.CurrentRoute, source.CurrentRoute, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': currentRoute differs from the source");
            }

            if (!string.Equals(SerializeSlots(item), SerializeSlots(source), StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': expectedSlots differ from the source");
            }

            if (item.ExpectedRecipe != null || item.PreviousTurn != null)
            {
                violations.Add($"'{item.Id}': a translation must carry neither a recipe nor a previous turn");
            }

            if (string.Equals(item.Locale, source.Locale, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': translated into the source's own locale '{source.Locale}'");
            }

            if (!string.Equals(item.Source, TranslationSource, StringComparison.Ordinal))
            {
                violations.Add($"'{item.Id}': source '{item.Source}' is not '{TranslationSource}'");
            }
        }

        return violations;
    }

    public static List<string> ValidateLocales(
        IReadOnlyList<TurnGoldsetItem> translations, IEnumerable<string> languagePackLocales)
    {
        var allowed = NonGermanTargetLocales(languagePackLocales).ToHashSet(StringComparer.Ordinal);

        return translations
            .Where(item => item.Locale == null || !allowed.Contains(item.Locale))
            .Select(item => $"'{item.Id}': locale '{item.Locale}' is neither an installed language pack nor a core language other than {GermanLocale}")
            .ToList();
    }

    public static List<string> FindLocalesBelowMinimum(
        IReadOnlyList<TurnGoldsetItem> translations,
        IEnumerable<TurnGoldsetItem> baseItems,
        IEnumerable<string> languagePackLocales)
    {
        var counts = baseItems.Concat(translations)
            .GroupBy(item => item.Locale ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return NonGermanTargetLocales(languagePackLocales)
            .Select(locale => (Locale: locale, Count: counts.GetValueOrDefault(locale)))
            .Where(entry => entry.Count < MinItemsPerLocale)
            .Select(entry => $"locale '{entry.Locale}' has {entry.Count} items (base + translations), fewer than {MinItemsPerLocale}")
            .ToList();
    }

    public static List<string> FindMessagesRepeatingAnotherMessage(
        IReadOnlyList<TurnGoldsetItem> translations, IEnumerable<string> otherGoldsetMessages)
    {
        var others = otherGoldsetMessages.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var violations = translations
            .Where(item => others.Contains(Normalize(item.Message)))
            .Select(item => $"'{item.Id}' repeats a message of another goldset")
            .ToList();

        violations.AddRange(translations
            .GroupBy(item => Normalize(item.Message), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"translations share a message: {string.Join(", ", group.Select(item => item.Id))}"));

        return violations;
    }

    public static string Normalize(string message) => message.Trim().ToLowerInvariant();

    private static string SerializeSlots(TurnGoldsetItem item) => JsonSerializer.Serialize(item.ExpectedSlots);
}
