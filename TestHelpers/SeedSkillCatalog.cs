// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Loads the real skill seed catalog (skill-seeds.json) as AgentSkill rows carrying their category, trigger
/// keywords and synonyms, so keyword-routing guards run against the shipped catalog instead of a hand-built one.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.TestHelpers;

public static class SeedSkillCatalog
{
    private const string SeedFileName = "skill-seeds.json";

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static List<AgentSkill> Load()
    {
        var path = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", SeedFileName);
        path.ShouldNotBeNull();

        var seed = JsonSerializer.Deserialize<SkillSeedFile>(File.ReadAllText(path), JsonReadOptions);

        return seed!.Skills
            .Select(definition => new AgentSkill
            {
                Name = definition.Name,
                Category = definition.Category,
                TriggerKeywords = TriggerKeywordFormat.Write(definition.TriggerKeywords),
                Synonyms = definition.Synonyms
            })
            .ToList();
    }
}
