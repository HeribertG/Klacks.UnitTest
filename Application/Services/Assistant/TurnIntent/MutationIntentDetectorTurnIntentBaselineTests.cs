// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reporting-only measurement of the REAL MutationIntentDetector (core stems plus the language-plugin keywords exactly
/// as Program.cs loads them) against the non-ambiguous items of the turn-intent labelset. Fixed mapping, declared
/// before the run: Do = IsMutationIntent; Ask = IsInformationQuestion and not IsMutationIntent; anything else is "no
/// decision". The detector is a precision-biased MUTATION signal, so read-type Do items ("Zeig mir ...") are missed by
/// design and every "no decision" counts as an error in the accuracy figures. Explicit because loading the plugins
/// leaks their question leads (raw StartsWith) into every later test of the same process.
/// </summary>

using System.Globalization;
using System.Text;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

[TestFixture]
[Explicit("Reporting only: loads the language-plugin keywords into the static MutationIntentDetector.")]
public class MutationIntentDetectorTurnIntentBaselineTests
{
    private const string NoDecision = "none";
    private const string AllLocales = "ALL";
    private const string PluginLocales = "PLUGINS";
    private const int MaxListedExamples = 400;

    private static readonly string[] CoreLocales = ["de", "en", "fr", "it"];

    [Test]
    public void Report_MutationIntentDetectorAgainstTurnIntentLabelset()
    {
        MutationIntentPluginLoader.Load(
            RepositoryRootLocator.ApiProject,
            (file, ex) => TestContext.Out.WriteLine($"Plugin file failed to load: {file} ({ex.Message})"));

        var items = TurnIntentGoldsetReader.LoadTrainable();
        var rows = items.Select(Evaluate).ToList();
        var report = new StringBuilder();

        report.AppendLine("=== MutationIntentDetector vs turn-intent-v1 (non-ambiguous items) ===");
        report.AppendLine("Mapping: Do = IsMutationIntent | Ask = IsInformationQuestion && !IsMutationIntent | else none");
        report.AppendLine($"Items: {rows.Count} (Ask {Count(rows, TurnIntentLabels.Ask)}, Do {Count(rows, TurnIntentLabels.Do)}, Other {Count(rows, TurnIntentLabels.Other)})");
        report.AppendLine();

        AppendConfusion(report, rows);
        AppendPerLocale(report, rows);
        AppendDoByKind(report, rows);
        AppendDataQuestions(report, rows);
        AppendMinimalPairs(report, rows);
        AppendExamples(report, rows);

        TestContext.Out.WriteLine(report.ToString());
        rows.ShouldNotBeEmpty();
    }

    private static Row Evaluate(TurnIntentGoldsetItem item)
    {
        var mutation = MutationIntentDetector.IsMutationIntent(item.Text);
        var question = MutationIntentDetector.IsInformationQuestion(item.Text);
        var decision = mutation ? TurnIntentLabels.Do : question ? TurnIntentLabels.Ask : NoDecision;
        return new Row(item, mutation, question, decision);
    }

    private static int Count(IEnumerable<Row> rows, string label) => rows.Count(row => row.Item.Label == label);

    private static string Pct(int numerator, int denominator) =>
        denominator == 0
            ? "  n/a"
            : (100.0 * numerator / denominator).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5) + "%";

    private static int LocaleOrder(string locale) => CoreLocales.Contains(locale) ? 0 : 1;

    private static void AppendConfusion(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("--- Confusion (rows = label, columns = detector decision) ---");
        report.AppendLine($"{"label",-8}{"Ask",8}{"Do",8}{NoDecision,8}");
        foreach (var label in TurnIntentLabels.All)
        {
            var subset = rows.Where(row => row.Item.Label == label).ToList();
            report.AppendLine(
                $"{label,-8}{subset.Count(r => r.Decision == TurnIntentLabels.Ask),8}" +
                $"{subset.Count(r => r.Decision == TurnIntentLabels.Do),8}{subset.Count(r => r.Decision == NoDecision),8}");
        }

        var askDo = rows.Where(r => r.Item.Label != TurnIntentLabels.Other).ToList();
        var correct = askDo.Count(r => r.Decision == r.Item.Label);
        var decided = askDo.Where(r => r.Decision != NoDecision).ToList();
        var decidedCorrect = decided.Count(r => r.Decision == r.Item.Label);
        var threeClass = rows.Count(r => (r.Decision == NoDecision ? TurnIntentLabels.Other : r.Decision) == r.Item.Label);

        report.AppendLine($"Ask/Do accuracy over all Ask+Do items (no decision = error): {correct}/{askDo.Count} = {Pct(correct, askDo.Count)}");
        report.AppendLine($"Ask/Do accuracy over decided Ask+Do items only: {decidedCorrect}/{decided.Count} = {Pct(decidedCorrect, decided.Count)}");
        report.AppendLine($"3-class accuracy with 'none' counted as Other: {threeClass}/{rows.Count} = {Pct(threeClass, rows.Count)}");
        report.AppendLine();
    }

    private static void AppendPerLocale(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("--- Per locale ---");
        report.AppendLine("AskFP = Ask flagged IsMutationIntent | OtherFP = Other flagged IsMutationIntent | DoRecall = Do flagged IsMutationIntent | DoIsQ = Do that IsInformationQuestion | AskIsQ = Ask that IsInformationQuestion | AskDo-acc = mapping accuracy over Ask+Do");
        report.AppendLine($"{"locale",-8}{"nAsk",5}{"nDo",5}{"nOth",5}  {"AskFP",14}  {"OtherFP",14}  {"DoRecall",14}  {"DoIsQ",14}  {"AskIsQ",14}  {"AskDo-acc",14}");

        var locales = rows.Select(r => r.Item.Locale).Distinct()
            .OrderBy(LocaleOrder).ThenBy(l => l, StringComparer.Ordinal).ToList();
        foreach (var locale in locales)
        {
            AppendLocaleRow(report, locale, rows.Where(r => r.Item.Locale == locale).ToList());
        }

        AppendLocaleRow(report, PluginLocales, rows.Where(r => !CoreLocales.Contains(r.Item.Locale)).ToList());
        AppendLocaleRow(report, AllLocales, rows);
        report.AppendLine();
    }

    private static string Cell(int numerator, int denominator) => $"{numerator,3}/{denominator,-3} {Pct(numerator, denominator)}";

    private static void AppendLocaleRow(StringBuilder report, string name, List<Row> subset)
    {
        var ask = subset.Where(r => r.Item.Label == TurnIntentLabels.Ask).ToList();
        var doItems = subset.Where(r => r.Item.Label == TurnIntentLabels.Do).ToList();
        var other = subset.Where(r => r.Item.Label == TurnIntentLabels.Other).ToList();
        var askDo = ask.Concat(doItems).ToList();

        report.AppendLine(
            $"{name,-8}{ask.Count,5}{doItems.Count,5}{other.Count,5}  {Cell(ask.Count(r => r.Mutation), ask.Count),14}  {Cell(other.Count(r => r.Mutation), other.Count),14}  " +
            $"{Cell(doItems.Count(r => r.Mutation), doItems.Count),14}  {Cell(doItems.Count(r => r.Question), doItems.Count),14}  {Cell(ask.Count(r => r.Question), ask.Count),14}  " +
            $"{Cell(askDo.Count(r => r.Decision == r.Item.Label), askDo.Count),14}");
    }

    private static void AppendDoByKind(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("--- Do items by item kind (no mutation-vs-read label exists in the set) ---");
        foreach (var group in rows.Where(r => r.Item.Label == TurnIntentLabels.Do).GroupBy(r => r.Item.Kind).OrderByDescending(g => g.Count()))
        {
            report.AppendLine(
                $"{group.Key,-16} n={group.Count(),3}  IsMutationIntent {Cell(group.Count(r => r.Mutation), group.Count())}  " +
                $"IsInformationQuestion {Cell(group.Count(r => r.Question), group.Count())}");
        }

        report.AppendLine();
    }

    private static void AppendDataQuestions(StringBuilder report, List<Row> rows)
    {
        var dataQuestions = rows
            .Where(r => r.Item.Label == TurnIntentLabels.Do
                        && (r.Item.Kind == TurnIntentLabels.DataQuestionKind || r.Item.Subkind == TurnIntentLabels.DataQuestionKind))
            .ToList();

        report.AppendLine("--- Do data-questions (kind or subkind = data-question): labelled Do, read as information questions ---");
        report.AppendLine(
            $"n={dataQuestions.Count}  IsInformationQuestion {Cell(dataQuestions.Count(r => r.Question), dataQuestions.Count)}  " +
            $"IsMutationIntent {Cell(dataQuestions.Count(r => r.Mutation), dataQuestions.Count)}");
        report.AppendLine("Every IsInformationQuestion hit here is classified Ask by the fixed mapping, i.e. wrong.");
        report.AppendLine();
    }

    private static void AppendMinimalPairs(StringBuilder report, List<Row> rows)
    {
        var pairs = rows
            .Where(r => r.Item.PairId != null)
            .GroupBy(r => (r.Item.PairId, r.Item.Locale))
            .Where(g => g.Select(r => r.Item.Label).Distinct().Count() == 2)
            .ToList();

        var bothCorrect = pairs.Count(g => g.All(r => r.Decision == r.Item.Label));
        report.AppendLine("--- Minimal pairs (pair x locale with both members non-ambiguous) ---");
        report.AppendLine($"both members decided correctly: {bothCorrect}/{pairs.Count} = {Pct(bothCorrect, pairs.Count)}");
        foreach (var locale in pairs.Select(g => g.Key.Locale).Distinct().OrderBy(LocaleOrder).ThenBy(l => l, StringComparer.Ordinal))
        {
            var perLocale = pairs.Where(g => g.Key.Locale == locale).ToList();
            var ok = perLocale.Count(g => g.All(r => r.Decision == r.Item.Label));
            report.AppendLine($"  {locale,-6} {Cell(ok, perLocale.Count)}");
        }

        report.AppendLine();
    }

    private static void AppendExamples(StringBuilder report, List<Row> rows)
    {
        Section(report, "False positives: Ask or Other flagged IsMutationIntent",
            rows.Where(r => r.Item.Label != TurnIntentLabels.Do && r.Mutation));
        Section(report, "Do items missed by IsMutationIntent, locales de/en/fr/it",
            rows.Where(r => r.Item.Label == TurnIntentLabels.Do && !r.Mutation && CoreLocales.Contains(r.Item.Locale)));
        Section(report, "Do items missed by IsMutationIntent, plugin locales",
            rows.Where(r => r.Item.Label == TurnIntentLabels.Do && !r.Mutation && !CoreLocales.Contains(r.Item.Locale)));
    }

    private static void Section(StringBuilder report, string title, IEnumerable<Row> subset)
    {
        var list = subset.ToList();
        report.AppendLine($"--- {title} ({list.Count}) ---");
        foreach (var row in list.Take(MaxListedExamples))
        {
            report.AppendLine($"[{row.Item.Locale}] ({row.Item.Kind}) {row.Item.Text}");
        }

        report.AppendLine();
    }

    private sealed record Row(TurnIntentGoldsetItem Item, bool Mutation, bool Question, string Decision);
}
