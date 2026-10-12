// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards against a changed skill seed that keeps its version. SkillSeedLoader.SyncExistingAsync only rewrites an
/// existing skill row when the seed version rises above the stored SeedVersion, so a gate or definition edit without
/// a bump ships in the file but never reaches an existing database (accept_scenario/reject_scenario changed their
/// gate at version 4 that way). The snapshot lives next to skill-seeds.json in Klacks.Api (excluded from the build
/// output), so it is versioned and merged atomically with the seeds it pins. It records, per seeded skill of the core seeds and the messaging
/// plugin seeds, the version and a hash of the whole definition except the version (canonical JSON, sorted keys, so
/// formatting and line endings do not count). A definition whose hash moved at the same version fails with "bump the
/// version"; a raised version, a new or a removed skill fails until the snapshot is regenerated, so the snapshot
/// always pins the current state and the next unbumped edit is caught again.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SkillSeedVersionBumpGuardTests
{
    private const string VersionProperty = "version";
    private const string NameProperty = "name";
    private const string SkillsProperty = "skills";
    private const string HashProperty = "hash";

    private const string RegenerateInstruction =
        "Regenerate the snapshot AFTER bumping every changed skill: dotnet test Klacks.UnitTest/Klacks.UnitTest.csproj " +
        "--filter \"FullyQualifiedName~SkillSeedVersionBumpGuardTests.RegenerateSnapshot\" (explicit test, rewrites " +
        "Klacks.Api/Application/Skills/Definitions/skill-seed-versions.json), then commit the snapshot together with " +
        "the seed change in Klacks.Api.";

    private static readonly string[] SnapshotRelativePath =
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seed-versions.json"];

    private static readonly string[][] SeedRelativePaths =
    [
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"],
        ["Klacks.Api", "Plugins", "Features", "messaging", "skill-seeds.json"]
    ];

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions SnapshotWriteOptions = new() { WriteIndented = true };

    private sealed record SeedFingerprint(int Version, string Hash);

    [Test]
    public void EveryChangedSkillDefinition_RaisedItsVersion()
    {
        var current = LoadCurrentFingerprints();
        var snapshot = LoadSnapshot();
        var failures = new List<string>();

        foreach (var (name, now) in current.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!snapshot.TryGetValue(name, out var pinned))
            {
                failures.Add($"'{name}' is new and not in the snapshot yet.");
                continue;
            }

            if (now.Version < pinned.Version)
            {
                failures.Add($"'{name}' went DOWN from version {pinned.Version} to {now.Version}; existing databases keep the newer row.");
            }
            else if (now.Version == pinned.Version && now.Hash != pinned.Hash)
            {
                failures.Add(
                    $"'{name}' changed its definition but kept version {now.Version}. Bump it to {now.Version + 1}: " +
                    "SkillSeedLoader skips a seed whose version did not increase, so the change would never reach an existing database.");
            }
            else if (now.Version > pinned.Version)
            {
                failures.Add($"'{name}' was bumped from {pinned.Version} to {now.Version}; the snapshot still pins {pinned.Version}.");
            }
        }

        failures.AddRange(snapshot.Keys
            .Where(name => !current.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => $"'{name}' is in the snapshot but no longer seeded."));

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Append(RegenerateInstruction)));
    }

    [Test]
    [Explicit("Rewrites the snapshot from the current seeds; run it on purpose after bumping every changed skill.")]
    public void RegenerateSnapshot()
    {
        var current = LoadCurrentFingerprints();
        var skills = new JsonObject();
        foreach (var (name, fingerprint) in current.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            skills[name] = new JsonObject
            {
                [VersionProperty] = fingerprint.Version,
                [HashProperty] = fingerprint.Hash
            };
        }

        var document = new JsonObject { [SkillsProperty] = skills };
        var path = Path.Combine([RepositoryRootLocator.RequireRoot(), .. SnapshotRelativePath]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, document.ToJsonString(SnapshotWriteOptions) + Environment.NewLine);
    }

    private static Dictionary<string, SeedFingerprint> LoadCurrentFingerprints()
    {
        var fingerprints = new Dictionary<string, SeedFingerprint>(StringComparer.Ordinal);
        foreach (var relativePath in SeedRelativePaths)
        {
            var root = JsonNode.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(relativePath)))!;
            var skillList = root is JsonArray pluginSeeds ? pluginSeeds : root[SkillsProperty]!.AsArray();
            foreach (var skill in skillList)
            {
                var definition = skill!.AsObject();
                var name = definition[NameProperty]!.GetValue<string>();
                var version = definition[VersionProperty]!.GetValue<int>();
                fingerprints.ContainsKey(name).ShouldBeFalse($"Skill '{name}' is seeded twice.");
                fingerprints[name] = new SeedFingerprint(version, HashWithoutVersion(definition));
            }
        }

        return fingerprints;
    }

    private static Dictionary<string, SeedFingerprint> LoadSnapshot()
    {
        var path = RepositoryRootLocator.FindFile(SnapshotRelativePath);
        path.ShouldNotBeNull($"Snapshot missing. {RegenerateInstruction}");

        var skills = JsonNode.Parse(File.ReadAllText(path))![SkillsProperty]!.AsObject();
        return skills.ToDictionary(
            entry => entry.Key,
            entry => new SeedFingerprint(
                entry.Value![VersionProperty]!.GetValue<int>(),
                entry.Value![HashProperty]!.GetValue<string>()),
            StringComparer.Ordinal);
    }

    private static string HashWithoutVersion(JsonObject definition)
    {
        var canonical = Canonicalize(definition)!.AsObject();
        canonical.Remove(VersionProperty);
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(CanonicalOptions));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Canonicalize(property.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString())
    };
}
