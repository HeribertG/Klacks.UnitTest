// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every UI label a curated knowledge happen quotes as a language anchor (de: "Label", en: "Label",
/// also grouped as de/en: "Label") is a real value of that language's Klacks.Ui catalogue. A knowledge body that
/// cites a label the UI does not show makes the assistant name buttons the user cannot find. Quotes are compared
/// after normalisation (whitespace collapsed, interpolation placeholders reduced to {}, leading placeholders and
/// trailing ellipses removed) so line-wrapped quotes and quoted fragments of interpolated labels still match.
/// Quotes that name domain terms or sentence fragments instead of UI labels are listed in
/// <see cref="NonLabelQuotes"/>; an entry that no longer applies fails the stale-entry test. The Klacks.Ui half
/// reports inconclusive when that repository is not checked out next to Klacks.Api.
/// </summary>

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class KnowledgeHappenUiLabelParityGuardTests
{
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string KnowledgeRelativePath = "Klacks.Api/Infrastructure/Persistence/Seed/KlacksyKnowledge";
    private const string MarkdownSearchPattern = "*.md";
    private const string JsonExtension = ".json";
    private const string LanguageGroupName = "languages";
    private const string TextGroupName = "text";
    private const char LanguageSeparator = '/';
    private const string EscapedQuote = "\\\"";
    private const string Quote = "\"";
    private const string PlaceholderReplacement = "{}";
    private const string SingleSpace = " ";
    private const string EllipsisCharacter = "…";
    private const string EllipsisDots = "...";
    private const string KeySeparator = "|";
    private const int MinimumAnchorCount = 1250;

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly (string File, string Language, string Text)[] NonLabelQuotes =
    [
        ("19-page-period-closing.md", "en", "... Non-closed shifts will NOT be exported."),
        ("69-shift-classification.md", "de", "Frühdienst"),
        ("69-shift-classification.md", "de", "Spätdienst"),
        ("69-shift-classification.md", "en", "early shift"),
        ("69-shift-classification.md", "en", "late shift"),
        ("69-shift-classification.md", "fr", "service du matin"),
        ("69-shift-classification.md", "fr", "service du soir"),
        ("69-shift-classification.md", "it", "turno mattutino"),
        ("69-shift-classification.md", "it", "turno serale"),
    ];

    private static readonly Regex AnchorRegex = new(
        @"\b(?<languages>(?:de|en|fr|it)(?:/(?:de|en|fr|it))*):\s*""(?<text>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new(@"\{\{[^}]*\}\}|\{[^}]*\}", RegexOptions.Compiled);

    private static readonly Regex LeadingPlaceholderRegex = new(@"^(?:\{\}\s*)+", RegexOptions.Compiled);

    private static readonly Regex TrailingEllipsisRegex = new(@"(?:…|\.\.\.)+$", RegexOptions.Compiled);

    private sealed record Anchor(string File, int Line, string Language, string Text);

    [Test]
    public void KnowledgeHappens_MustQuoteEnoughLanguageAnchors()
    {
        var anchors = ParseAnchors();

        anchors.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumAnchorCount,
            "The anchor regex found suspiciously few language anchors; it may have stopped matching the " +
            "happen format (de: \"Label\", en: \"Label\", de/en: \"Label\").");
    }

    [Test]
    public void EveryQuotedLabel_MustBeAValueOfTheUiCatalogue()
    {
        var catalogues = LoadCatalogues();
        if (catalogues == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var exceptions = ExceptionKeys();
        var misses = ParseAnchors()
            .Where(anchor => !catalogues[anchor.Language].Contains(anchor.Text))
            .Where(anchor => !exceptions.Contains(KeyOf(anchor.File, anchor.Language, anchor.Text)))
            .Select(anchor => $"{anchor.File}:{anchor.Line} [{anchor.Language}] \"{anchor.Text}\"")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        misses.ShouldBeEmpty(
            "These knowledge anchors quote a label that is not a value of the Klacks.Ui catalogue of that " +
            "language. Use the exact UI text, or reword the sentence so it does not quote a label that does " +
            "not exist: " + Environment.NewLine + string.Join(Environment.NewLine, misses));
    }

    [Test]
    public void NonLabelQuotes_MustStillApply()
    {
        var catalogues = LoadCatalogues();
        if (catalogues == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var stillMissing = ParseAnchors()
            .Where(anchor => !catalogues[anchor.Language].Contains(anchor.Text))
            .Select(anchor => KeyOf(anchor.File, anchor.Language, anchor.Text))
            .ToHashSet(StringComparer.Ordinal);

        var stale = ExceptionKeys()
            .Where(key => !stillMissing.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "These exception entries no longer match a missing anchor (the quote was fixed, changed or " +
            "removed) and must be deleted from NonLabelQuotes: " + string.Join(", ", stale));
    }

    private static HashSet<string> ExceptionKeys() =>
        NonLabelQuotes
            .Select(entry => KeyOf(entry.File, entry.Language, entry.Text))
            .ToHashSet(StringComparer.Ordinal);

    private static string KeyOf(string file, string language, string text) =>
        string.Join(KeySeparator, file, language, text);

    private static string Normalize(string text)
    {
        var collapsed = WhitespaceRegex.Replace(text, SingleSpace).Trim();
        var withoutPlaceholders = PlaceholderRegex.Replace(collapsed, PlaceholderReplacement);
        var withoutEllipsis = TrailingEllipsisRegex.Replace(withoutPlaceholders, string.Empty).Trim();

        return LeadingPlaceholderRegex.Replace(withoutEllipsis, string.Empty).Trim();
    }

    private static List<Anchor> ParseAnchors()
    {
        var knowledgeDirectory = RepositoryRootLocator.RequireDirectory(KnowledgeRelativePath);
        var anchors = new List<Anchor>();

        foreach (var path in Directory.EnumerateFiles(knowledgeDirectory, MarkdownSearchPattern).OrderBy(p => p, StringComparer.Ordinal))
        {
            var content = File.ReadAllText(path, Encoding.UTF8);
            var fileName = Path.GetFileName(path);

            foreach (Match match in AnchorRegex.Matches(content))
            {
                var line = CountLines(content, match.Index);
                var text = Normalize(match.Groups[TextGroupName].Value.Replace(EscapedQuote, Quote));

                foreach (var language in match.Groups[LanguageGroupName].Value.Split(LanguageSeparator))
                {
                    anchors.Add(new Anchor(fileName, line, language, text));
                }
            }
        }

        return anchors;
    }

    private static int CountLines(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static Dictionary<string, HashSet<string>>? LoadCatalogues()
    {
        var directory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (directory == null)
        {
            return null;
        }

        var catalogues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var language in CoreLanguages)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, language + JsonExtension), Encoding.UTF8));
            var values = new HashSet<string>(StringComparer.Ordinal);
            CollectStrings(document.RootElement, values);
            catalogues[language] = values;
        }

        return catalogues;
    }

    private static void CollectStrings(JsonElement element, HashSet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                values.Add(Normalize(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStrings(property.Value, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, values);
                }

                break;
        }
    }
}
