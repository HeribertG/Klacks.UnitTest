// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard: every language pack that ships recipe anchors (Klacks.Api/Plugins/Languages/code
/// with a recipe-anchors.json) is classified in exactly one match mode of RecipeAnchorMatchLanguages,
/// the substring and word-start sets are disjoint, and neither set names a code without such a pack. The
/// matcher treats an unclassified code as word-start, which silently loses every anchor of a script without
/// word boundaries (km, my); this guard makes a new pack fail until its author classifies it consciously.
/// Codes compare case-insensitively, like the sets and the anchor lookup.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class RecipeAnchorMatchLanguagesGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagesRelativePath = "Plugins/Languages";
    private const string RecipeAnchorsFileName = "recipe-anchors.json";
    private const string SyntheticUnclassifiedCode = "km";

    private static DirectoryInfo LanguagesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory, LanguagesRelativePath);
            if (Directory.Exists(candidate))
            {
                return new DirectoryInfo(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{LanguagesRelativePath} from the test base directory.");
    }

    private static IReadOnlyList<string> AnchorPackCodes() =>
        LanguagesRoot().GetDirectories()
            .Where(directory => File.Exists(Path.Combine(directory.FullName, RecipeAnchorsFileName)))
            .Select(directory => directory.Name)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

    [Test]
    public void EveryAnchorPack_IsClassifiedInExactlyOneMatchMode_AndEveryClassifiedCodeHasAPack()
    {
        var packs = AnchorPackCodes();
        packs.ShouldNotBeEmpty($"No {RecipeAnchorsFileName} found under {LanguagesRelativePath}; the guard would pass vacantly.");

        var violations = FindViolations(
            packs,
            RecipeAnchorMatchLanguages.SubstringMatchLanguages,
            RecipeAnchorMatchLanguages.WordStartMatchLanguages);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void AnUnclassifiedPack_IsReported()
    {
        var packs = AnchorPackCodes().Append(SyntheticUnclassifiedCode).ToList();

        var violations = FindViolations(
            packs,
            RecipeAnchorMatchLanguages.SubstringMatchLanguages,
            RecipeAnchorMatchLanguages.WordStartMatchLanguages);

        violations.ShouldHaveSingleItem().ShouldContain($"'{SyntheticUnclassifiedCode}'");
    }

    [Test]
    public void ACodeInBothSets_AndACodeWithoutPack_AreReported()
    {
        var packs = new[] { "es" };
        var substring = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "es", "zz" };
        var wordStart = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ES" };

        var violations = FindViolations(packs, substring, wordStart);

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("'es'", StringComparison.Ordinal));
        violations.ShouldContain(v => v.Contains("'zz'", StringComparison.Ordinal));
    }

    private static List<string> FindViolations(
        IReadOnlyCollection<string> packCodes,
        IReadOnlySet<string> substringLanguages,
        IReadOnlySet<string> wordStartLanguages)
    {
        var violations = new List<string>();

        foreach (var code in packCodes)
        {
            var inSubstring = substringLanguages.Contains(code);
            var inWordStart = wordStartLanguages.Contains(code);

            if (inSubstring && inWordStart)
            {
                violations.Add(
                    $"Language pack '{code}' is in both SubstringMatchLanguages and WordStartMatchLanguages of " +
                    $"{nameof(RecipeAnchorMatchLanguages)}; keep it in exactly one.");
            }
            else if (!inSubstring && !inWordStart)
            {
                violations.Add(
                    $"Language pack '{code}' ships {RecipeAnchorsFileName} but is not classified in " +
                    $"{nameof(RecipeAnchorMatchLanguages)}. Classify the new language consciously: scripts without " +
                    "word boundaries (e.g. km, my, ja, th) and languages whose compounds end in the head noun " +
                    "need SubstringMatchLanguages; languages that separate words with spaces and put the stem " +
                    "first belong in WordStartMatchLanguages. Unclassified codes silently fall back to word-start.");
            }
        }

        var orphans = substringLanguages.Concat(wordStartLanguages)
            .Where(code => !packCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var code in orphans)
        {
            violations.Add(
                $"{nameof(RecipeAnchorMatchLanguages)} names '{code}', but no {LanguagesRelativePath}/{code}/" +
                $"{RecipeAnchorsFileName} exists; remove the code or ship the pack.");
        }

        return violations;
    }
}
