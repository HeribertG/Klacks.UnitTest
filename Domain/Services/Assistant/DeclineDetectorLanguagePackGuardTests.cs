// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard tests over the shipped vocabulary instead of hand-picked examples: every negation and every
/// decline phrase of every language pack must be recognized as a bare refusal, and the core-language
/// file the detector is fed at startup must exist, parse and reach the build output. An entry that is
/// not recognized is invisible in production - the recipe confirmation gate of that language stays
/// pending for good - and no example-based test can catch that across 21 packs.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Klacksy;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class DeclineDetectorLanguagePackGuardTests
{
    private const string ConversationSignalsFileName = "conversation-signals.json";
    private const string CoreSignalsFileName = "conversation-signals-core.json";
    private const string NegationsKey = "negations";
    private const string DeclinesKey = "declines";
    private const string SentenceEnd = ".";

    private static readonly string[] PluginsLanguagesRelativePath = { "Klacks.Api", "Plugins", "Languages" };
    private static readonly string[] CoreSignalsRelativePath = { "Klacks.Api", "Application", "Klacksy" };

    // The core vocabulary is process-wide static state that CoreConversationSignalsInstaller installs
    // once per process, and the per-pack test clears it to isolate each pack. Reinstalling it here rather
    // than at the end of that test body is deliberate: a failure mid-loop would otherwise leave every
    // fixture running afterwards without a single core refusal, and they would fail for the wrong reason.
    [TearDown]
    public void ResetPluginEntries()
    {
        DeclineDetector.Reset();
        DeclineDetector.ResetCore();
        ConversationSignalsPluginLoader.LoadCore(AppContext.BaseDirectory);
    }

    // Each pack is configured on its own and discarded again, core vocabulary included, so neither an
    // entry of another pack nor a core token can stand in for a pack entry that does not match by itself:
    // "nie" is a core negation and a Polish pack entry at once, so with the core left in place the Polish
    // pack would have been certified on vocabulary that ships outside it. The three shapes are the ones a
    // real reply has: the bare entry, the entry with a sentence terminator, and the entry as the input
    // field capitalizes it.
    [Test]
    public void EveryLanguagePack_NegationsAndDeclines_AreRecognizedAsBareRefusals()
    {
        var violations = new List<string>();

        foreach (var packDir in PackDirectories())
        {
            var code = new DirectoryInfo(packDir).Name;
            var signals = ReadSignals(Path.Combine(packDir, ConversationSignalsFileName));
            var negations = ReadList(signals, NegationsKey);
            var declines = ReadList(signals, DeclinesKey);

            DeclineDetector.Reset();
            DeclineDetector.ResetCore();
            DeclineDetector.Configure(negations, declines);

            foreach (var entry in negations.Concat(declines).Where(entry => !string.IsNullOrWhiteSpace(entry)))
            {
                foreach (var message in new[] { entry, entry + SentenceEnd, SentenceCase(entry) })
                {
                    if (!DeclineDetector.IsBareNegation(message))
                    {
                        violations.Add($"{code}: '{message}' is not recognized as a bare refusal");
                    }
                }
            }

            DeclineDetector.Reset();
        }

        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    // The word pattern used to match letters only, so a word written with a combining mark lost it and
    // was cut short: the Thai refusal tokenized one character shorter than the vocabulary entry and
    // matched nothing. Two tokens force the token path, the one the prefix match cannot stand in for.
    [Test]
    public void ARefusalWrittenWithACombiningMark_IsTokenizedAsOneWord()
    {
        const string thaiRefusal = "ไม่";

        DeclineDetector.Configure([thaiRefusal], []);

        DeclineDetector.IsBareNegation($"{thaiRefusal} {thaiRefusal}").ShouldBeTrue();
    }

    [Test]
    public void AMultiWordPhrase_IsABareRefusalOnlyWhenNothingFollowsIt()
    {
        DeclineDetector.Configure([], ["ahora no"]);

        DeclineDetector.IsBareNegation("Ahora no").ShouldBeTrue();
        DeclineDetector.IsBareNegation("Ahora no, muéstrame los clientes").ShouldBeFalse();
    }

    [Test]
    public void TheCoreSignalsFile_ShipsANegationListPerLanguage()
    {
        var file = Path.Combine(LocateRepositoryDirectory(CoreSignalsRelativePath), CoreSignalsFileName);
        File.Exists(file).ShouldBeTrue($"{CoreSignalsFileName} holds the detector's whole core vocabulary");

        var byLanguage = ReadCoreSignals(file);
        byLanguage.Count.ShouldBeGreaterThan(0);

        var violations = new List<string>();
        foreach (var (code, signals) in byLanguage)
        {
            var negations = ReadList(signals, NegationsKey);
            if (negations.Count == 0)
            {
                violations.Add($"{code}: list '{NegationsKey}' is missing or empty");
                continue;
            }

            violations.AddRange(negations
                .Where(entry => entry != entry.Trim().ToLowerInvariant())
                .Select(entry => $"{code}/{NegationsKey}: entry '{entry}' is not trimmed lowercase"));

            if (negations.Distinct(StringComparer.Ordinal).Count() != negations.Count)
            {
                violations.Add($"{code}/{NegationsKey}: contains duplicate entries");
            }
        }

        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    // The core vocabulary is data now, so it can go missing in a way a compiled list never could: without
    // its copy entry in the project file it never reaches the output directory, LoadCore finds nothing and
    // every core-language refusal becomes undetectable in production without a single test turning red.
    // The expected entries are read from the repository file, the detector is fed through LoadCore from
    // the deployed one - exactly as Program.cs does it - so a wrong path or a missing copy both fail here.
    [Test]
    public void LoadCore_InstallsEveryCoreNegationFromTheDeployedFile()
    {
        var expected = ReadCoreSignals(
                Path.Combine(LocateRepositoryDirectory(CoreSignalsRelativePath), CoreSignalsFileName))
            .Values
            .SelectMany(signals => ReadList(signals, NegationsKey))
            .ToList();
        expected.Count.ShouldBeGreaterThan(0);

        DeclineDetector.Reset();
        ConversationSignalsPluginLoader.LoadCore(AppContext.BaseDirectory);

        var unrecognized = expected.Where(entry => !DeclineDetector.IsBareNegation(entry)).ToList();

        unrecognized.ShouldBeEmpty(
            $"LoadCore did not install these core refusals: {string.Join(", ", unrecognized)}. Either " +
            $"{CoreSignalsFileName} is missing its copy entry in Klacks.Api.csproj, or the path LoadCore " +
            "builds does not match where the file is deployed");
    }

    [Test]
    public void LoadCore_DoesNotPullInTheLanguagePacks()
    {
        DeclineDetector.Reset();

        ConversationSignalsPluginLoader.LoadCore(AppContext.BaseDirectory);

        DeclineDetector.LeadsWithNegation("Nie, dziękuję.").ShouldBeFalse();
    }

    /// <summary>
    /// A missing core file has to be reported rather than returned from silently. The vocabulary is data
    /// that only reaches the output directory through a copy entry in the project file, so losing it
    /// disables every core-language refusal at once - and the silent return made that indistinguishable
    /// from a healthy start, in the one place a deployment mistake of this kind shows up at all.
    /// </summary>
    [Test]
    public void LoadCore_WithNoCoreFilePresent_ReportsTheMissingFileThroughOnError()
    {
        var emptyDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(emptyDirectory);

        try
        {
            var failures = new List<KeyValuePair<string, Exception>>();

            ConversationSignalsPluginLoader.LoadCore(
                emptyDirectory, (file, error) => failures.Add(new KeyValuePair<string, Exception>(file, error)));

            failures.Count.ShouldBe(1);
            failures[0].Value.ShouldBeOfType<FileNotFoundException>();
            failures[0].Key.ShouldEndWith(CoreSignalsFileName);
        }
        finally
        {
            Directory.Delete(emptyDirectory, recursive: true);
        }
    }

    private static IEnumerable<string> PackDirectories() =>
        Directory.GetDirectories(LocateRepositoryDirectory(PluginsLanguagesRelativePath))
            .Where(dir => File.Exists(Path.Combine(dir, ConversationSignalsFileName)))
            .OrderBy(dir => dir, StringComparer.Ordinal);

    private static string SentenceCase(string entry) =>
        char.ToUpperInvariant(entry[0]) + entry[1..];

    private static Dictionary<string, List<string>> ReadSignals(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))
        ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);

    private static Dictionary<string, Dictionary<string, List<string>>> ReadCoreSignals(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(File.ReadAllText(file))
        ?? new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

    private static List<string> ReadList(Dictionary<string, List<string>> signals, string key) =>
        signals.TryGetValue(key, out var entries) ? entries : [];

    private static string LocateRepositoryDirectory(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"'{Path.Combine(relativePath)}' not found from the test base directory");
    }
}
