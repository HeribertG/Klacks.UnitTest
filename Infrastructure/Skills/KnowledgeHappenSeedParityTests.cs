// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parity gate between the curated knowledge markdown and the skill seeds. Every happen file under
/// KlacksyKnowledge must have a matching seed entry, and every seeded knowledge skill must have a
/// file — otherwise a happen is either an orphan (seeded into agent_memories but unreachable,
/// because no skill exposes it to the model) or a dead skill (registered but returning an error at
/// runtime because its memory key was never seeded). Both states existed undetected before
/// 2026-07-30. Also verifies that each file carries a frontmatter name, since the seeder silently
/// skips files without one, and that the seed's memoryKey equals the skill name.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class KnowledgeHappenSeedParityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillsJsonProperty = "skills";
    private const string SkillNameJsonProperty = "name";
    private const string HandlerTypeJsonProperty = "handlerType";
    private const string HandlerConfigJsonProperty = "handlerConfig";
    private const string MemoryKeyJsonProperty = "memoryKey";
    private const string KnowledgeHandlerType = "knowledge-happen";
    private const string ReadmeFileName = "README.md";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] KnowledgeRelativePath =
    [
        "Klacks.Api", "Infrastructure", "Persistence", "Seed", "KlacksyKnowledge"
    ];

    private static readonly Regex FrontmatterNameRegex = new(
        @"\A---\s*\r?\n(?<body>.+?)\r?\n---\s*\r?\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex NameFieldRegex = new(
        @"^name:\s*(?<value>\S+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static IReadOnlyDictionary<string, string> HappenKeysByFile()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(LocateDirectory(KnowledgeRelativePath), "*.md"))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals(ReadmeFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var frontmatter = FrontmatterNameRegex.Match(File.ReadAllText(path));
            var name = frontmatter.Success
                ? NameFieldRegex.Match(frontmatter.Groups["body"].Value)
                : Match.Empty;

            result[fileName] = name.Success ? name.Groups["value"].Value : string.Empty;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> SeededKnowledgeSkills()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var skill in document.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
        {
            if (!skill.TryGetProperty(HandlerTypeJsonProperty, out var handler)
                || handler.ValueKind != JsonValueKind.String
                || handler.GetString() != KnowledgeHandlerType)
            {
                continue;
            }

            var name = skill.GetProperty(SkillNameJsonProperty).GetString()!;
            var memoryKey = skill.TryGetProperty(HandlerConfigJsonProperty, out var config)
                            && config.ValueKind == JsonValueKind.Object
                            && config.TryGetProperty(MemoryKeyJsonProperty, out var key)
                ? key.GetString() ?? string.Empty
                : string.Empty;

            result[name] = memoryKey;
        }

        return result;
    }

    [Test]
    public void EveryHappenFile_MustCarryAFrontmatterName()
    {
        var nameless = HappenKeysByFile()
            .Where(kv => string.IsNullOrEmpty(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        nameless.ShouldBeEmpty(
            "These knowledge files have no 'name:' in their frontmatter: " + string.Join(", ", nameless) +
            ". KlacksyKnowledgeMemorySeed skips such files without failing, so their content would " +
            "never reach the database.");
    }

    [Test]
    public void EveryHappenFile_MustHaveASeedEntry()
    {
        var seeded = SeededKnowledgeSkills().Keys.ToHashSet(StringComparer.Ordinal);

        var orphans = HappenKeysByFile()
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && !seeded.Contains(kv.Value))
            .Select(kv => $"{kv.Key} (key '{kv.Value}')")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        orphans.ShouldBeEmpty(
            "These knowledge happens are seeded into agent_memories but no skill exposes them, so the " +
            "assistant can never retrieve them: " + string.Join(", ", orphans) +
            $". Add an entry with handlerType '{KnowledgeHandlerType}' to {SkillSeedsFileName}.");
    }

    [Test]
    public void EverySeededKnowledgeSkill_MustHaveAHappenFile()
    {
        var keys = HappenKeysByFile().Values
            .Where(v => !string.IsNullOrEmpty(v))
            .ToHashSet(StringComparer.Ordinal);

        var dead = SeededKnowledgeSkills().Keys
            .Where(name => !keys.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        dead.ShouldBeEmpty(
            "These knowledge skills are registered but have no markdown file, so calling one returns " +
            "the 'knowledge entry is not available' error at runtime: " + string.Join(", ", dead) +
            ". Add the file under KlacksyKnowledge or remove the seed entry.");
    }

    [Test]
    public void EverySeededKnowledgeSkill_MustPointAtItsOwnName()
    {
        var mismatched = SeededKnowledgeSkills()
            .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.Ordinal))
            .Select(kv => $"{kv.Key} -> memoryKey '{kv.Value}'")
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        mismatched.ShouldBeEmpty(
            "The memoryKey must equal the skill name — the file's frontmatter name, the memory key and " +
            "the skill name are one identifier in three places. Mismatches: " + string.Join(", ", mismatched));
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        return Path.Combine(LocateDirectory(DefinitionsRelativePath), fileName);
    }

    private static string LocateDirectory(string[] relativePath)
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
            $"Could not locate {string.Join('/', relativePath)} by walking up from the test base directory.");
    }
}
