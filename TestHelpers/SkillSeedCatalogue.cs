// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Test helper that reads the core skill-seeds.json from the repository and hands out one seeded skill
/// definition by name, so a test can pin a seed property (gate, parameter, description) without
/// re-implementing the file lookup.
/// </summary>

using System.Text.Json;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.TestHelpers;

public static class SkillSeedCatalogue
{
    private const string SeedFileName = "skill-seeds.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static SkillSeedDefinition Get(string skillName)
    {
        var path = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", SeedFileName);
        path.ShouldNotBeNull();
        var file = JsonSerializer.Deserialize<SkillSeedFile>(File.ReadAllText(path), JsonOptions);
        return file!.Skills.Single(skill => skill.Name == skillName);
    }
}
