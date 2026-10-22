// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the fuzzy client search. IClientSearchRepository.SearchAsync is a plain text match; the
/// trigram and phonetic ranking is only reachable through IClientSearchRepository.SearchByFuzzyNameAsync (or the
/// IClientFuzzySearchService behind it), whose rows are similar names, not names the user said ("Mayer" for
/// "Meier"). A write that takes the lone fuzzy row writes on a person the user never named, so:
/// - a file that calls SearchByFuzzyNameAsync or uses IClientFuzzySearchService must be a skill whose seeded effect
///   is not Mutate and that no seeded recipe captures an id from, or be on the allowlist below with the reason why
///   its fuzzy rows can never become a write target;
/// - allowlist entries that no longer reach the fuzzy search must be removed (shrink-only).
/// A source scan is used because the call is in a method body, which reflection cannot see; the skill name and
/// effect come from SkillImplementationAttribute and skill-seeds.json (SkillGuardSources).
/// Not covered: plugin skills, and callers that take the lone row of the plain text search (SearchAsync) without
/// the Strict judgement of ClientResolver/ClientNameMatch - SearchAsync matches substrings inside words, so such a
/// caller must go through ClientResolver.
/// </summary>

using static Klacks.UnitTest.TestHelpers.SkillGuardSources;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ClientFuzzySearchGuardTests
{
    private const string FuzzySearchMethodMarker = "SearchByFuzzyNameAsync";
    private const string FuzzySearchServiceMarker = "IClientFuzzySearchService";
    private const int MinimumFuzzySearchFiles = 5;

    private static readonly string[] Markers = [FuzzySearchMethodMarker, FuzzySearchServiceMarker];

    private static readonly string[] DefinitionFiles =
    [
        "Application/Interfaces/IClientSearchRepository.cs",
        "Application/Interfaces/IClientFuzzySearchService.cs",
        "Infrastructure/Repositories/Staffs/ClientSearchRepository.cs",
        "Infrastructure/Repositories/Staffs/ClientFuzzySearchService.cs",
        "Infrastructure/Extensions/RepositoryServiceCollectionExtensions.cs",
    ];

    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.Ordinal)
    {
        ["Application/Skills/ClientResolver.cs"] =
            "judges every fuzzy row with ClientNameMatch under NameMatchPolicy.Strict and answers a similar name with " +
            "the name ask-back; its Lenient callers are checked by NameMatchPolicyLenientGuardTests",
        ["Application/Skills/SearchEmployeesSkill.cs"] =
            "captured by recipes, but only rows the user named (ClientNameMatch) go to Results; fuzzy rows are " +
            "listed under OtherMatches with names and client number but without the id, so no capture can take them",
        ["Application/Services/Assistant/ClientNameCandidateGrounder.cs"] =
            "prompt grounding only; a person found through a similar name is rendered as a name the user must " +
            "confirm, never as a spelling to copy into a tool call",
        ["Infrastructure/Repositories/Staffs/ClientFilterRepository.cs"] =
            "fuzzy fallback of the UI client list; the user picks the row on screen",
        ["Infrastructure/Services/Clients/ClientBaseQueryService.cs"] =
            "fuzzy fallback of the UI client list query; the user picks the row on screen",
    };

    [Test]
    public void FuzzyClientSearch_IsReachedOnlyByReadOnlyUncapturedSkillsOrAllowlistedFiles()
    {
        var effects = LoadSkillEffects();
        var skillNamesByType = LoadSkillNamesByTypeName();
        var capturingSkills = LoadCapturingRecipeSkills();
        capturingSkills.ShouldNotBeEmpty("recipe-seeds.json has no capturing step; the scan is not reading the real seeds.");

        var files = FuzzySearchFiles();
        files.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumFuzzySearchFiles,
            $"Only {files.Count} files reach the fuzzy client search; the scan cannot have read the real sources.");

        var violations = files
            .Where(file => !DefinitionFiles.Contains(file, StringComparer.Ordinal) && !Allowlist.ContainsKey(file))
            .Select(file => DescribeNonReadSkill(file, skillNamesByType, effects)
                            ?? DescribeCapturingSkill(file, skillNamesByType, capturingSkills))
            .Where(problem => problem != null)
            .ToList();

        violations.ShouldBeEmpty(
            "The fuzzy client search returns similar names, not the person the user named. Only read-only skills " +
            "that no recipe captures from may reach it; a write resolves the client through ClientResolver " +
            "(Strict). Violations: " + string.Join("; ", violations));
    }

    [Test]
    public void AllowlistedFiles_StillReachTheFuzzyClientSearch()
    {
        var files = FuzzySearchFiles().ToHashSet(StringComparer.Ordinal);

        var stale = Allowlist.Keys.Where(file => !files.Contains(file)).ToList();

        stale.ShouldBeEmpty(
            "These allowlist entries no longer reach the fuzzy client search and must be removed (shrink-only): " +
            string.Join(", ", stale));
    }

    [Test]
    public void PlainClientSearch_HasNoFuzzyFallback()
    {
        var repository = File.ReadAllText(Path.Combine(
            Klacks.UnitTest.TestHelpers.RepositoryRootLocator.ApiProject,
            "Infrastructure", "Repositories", "Staffs", "ClientSearchRepository.cs"));

        var searchAsyncBody = MethodBody(repository, "public async Task<ClientSearchResult> SearchAsync(");

        searchAsyncBody.ShouldNotContain(
            "Fuzzy",
            customMessage: "SearchAsync must stay a plain text match; the fuzzy ranking belongs to SearchByFuzzyNameAsync, " +
                           "whose callers this guard restricts.");
    }

    private static List<string> FuzzySearchFiles() =>
        Markers.SelectMany(marker => FilesContaining(marker)).Distinct(StringComparer.Ordinal).ToList();

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"'{signature}' not found; the guard reads the wrong file.");

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };

            if (depth == 0)
            {
                return source[open..(index + 1)];
            }
        }

        return source[open..];
    }
}
